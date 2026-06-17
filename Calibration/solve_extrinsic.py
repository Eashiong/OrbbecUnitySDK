# -*- coding: utf-8 -*-
"""
阶段四：离线求解 Orbbec 彩色相机 → 手机 AR 相机 的常量外参 T_arCam←orbbec。

输入：阶段三采集工具（CalibrationCapture.cs）落盘的单个 JSON 文件，结构：
{
  "created_at": "...",
  "rig_version": "v1",
  "board": {"squares_x","squares_y","square_len_m","marker_len_m","dictionary"},
  "samples": [
    {
      "index", "timestamp_ms",
      "phone_intr":  {fx,fy,cx,cy,w,h},
      "orbbec_intr": {fx,fy,cx,cy,w,h},
      "orbbec_intr_valid": bool,
      "ar_world_pose": {"pos":[x,y,z], "rot":[qx,qy,qz,qw]},
      "phone_image_jpg_base64":  "...",   # 左上原点 JPG，方向与 phone_intr 一致
      "orbbec_image_jpg_base64": "..."     # 左上原点 JPG，方向与 orbbec_intr 一致
    }, ...
  ]
}

处理流程（对应 相机标定.md 阶段四）：
  4.1 每帧：在两张图各检测 ChArUco → solvePnP 得到 T_phone←board / T_orbbec←board（OpenCV 右手系）
       T_phone←orbbec_i = T_phone←board · inv(T_orbbec←board)
  4.2 多帧鲁棒融合：平移取中位数；旋转用四元数特征向量平均；按几何角度剔除离群帧
  4.3 坐标系转换 OpenCV(右手,Y下) → Unity(左手,Y上)：T_unity = S · T_opencv · S, S=diag(1,-1,1)
  4.4 导出 orbbec_ar_extrinsic.json（行主序 16 元素）+ 误差/一致性报告

依赖：numpy, opencv-contrib-python（aruco 在 contrib 包里）。
用法：
  python solve_extrinsic.py --input <采集json> [--out-dir output] [--debug]
不带 --input 时默认读取脚本同目录下 input/ 里最新的 *.json，找不到则报错给出提示。
"""

import argparse
import base64
import datetime
import glob
import json
import os
import sys

import numpy as np

try:
    import cv2
    import cv2.aruco as aruco
except ImportError as e:  # pragma: no cover
    sys.stderr.write(
        "缺少依赖：请先安装 opencv-contrib-python 与 numpy\n"
        "    pip install -r requirements.txt\n"
        f"原始错误：{e}\n"
    )
    sys.exit(2)


# --------------------------------------------------------------------------- #
# 基础工具
# --------------------------------------------------------------------------- #

def log(msg):
    print(msg, flush=True)


def decode_jpg_base64_to_gray(b64):
    """base64 JPG → 灰度图 (np.uint8, HxW)。失败返回 None。"""
    try:
        raw = base64.b64decode(b64)
    except Exception:
        return None
    arr = np.frombuffer(raw, dtype=np.uint8)
    img = cv2.imdecode(arr, cv2.IMREAD_GRAYSCALE)
    return img


def make_T(R, t):
    """由 3x3 旋转 R 与 3 平移 t 组装 4x4 齐次矩阵。"""
    T = np.eye(4, dtype=np.float64)
    T[:3, :3] = R
    T[:3, 3] = np.asarray(t, dtype=np.float64).reshape(3)
    return T


def intr_to_K(intr):
    """内参字典 → 3x3 相机矩阵 K。"""
    return np.array(
        [
            [float(intr["fx"]), 0.0, float(intr["cx"])],
            [0.0, float(intr["fy"]), float(intr["cy"])],
            [0.0, 0.0, 1.0],
        ],
        dtype=np.float64,
    )


def rotmat_to_quat(R):
    """3x3 旋转矩阵 → 单位四元数 [w,x,y,z]（右手系）。"""
    R = np.asarray(R, dtype=np.float64)
    tr = R[0, 0] + R[1, 1] + R[2, 2]
    if tr > 0.0:
        s = np.sqrt(tr + 1.0) * 2.0
        w = 0.25 * s
        x = (R[2, 1] - R[1, 2]) / s
        y = (R[0, 2] - R[2, 0]) / s
        z = (R[1, 0] - R[0, 1]) / s
    elif R[0, 0] > R[1, 1] and R[0, 0] > R[2, 2]:
        s = np.sqrt(1.0 + R[0, 0] - R[1, 1] - R[2, 2]) * 2.0
        w = (R[2, 1] - R[1, 2]) / s
        x = 0.25 * s
        y = (R[0, 1] + R[1, 0]) / s
        z = (R[0, 2] + R[2, 0]) / s
    elif R[1, 1] > R[2, 2]:
        s = np.sqrt(1.0 + R[1, 1] - R[0, 0] - R[2, 2]) * 2.0
        w = (R[0, 2] - R[2, 0]) / s
        x = (R[0, 1] + R[1, 0]) / s
        y = 0.25 * s
        z = (R[1, 2] + R[2, 1]) / s
    else:
        s = np.sqrt(1.0 + R[2, 2] - R[0, 0] - R[1, 1]) * 2.0
        w = (R[1, 0] - R[0, 1]) / s
        x = (R[0, 2] + R[2, 0]) / s
        y = (R[1, 2] + R[2, 1]) / s
        z = 0.25 * s
    q = np.array([w, x, y, z], dtype=np.float64)
    q /= np.linalg.norm(q)
    if q[0] < 0:  # 统一半球，便于平均
        q = -q
    return q


def quat_to_rotmat(q):
    """单位四元数 [w,x,y,z] → 3x3 旋转矩阵。"""
    w, x, y, z = q / np.linalg.norm(q)
    return np.array(
        [
            [1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)],
            [2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)],
            [2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)],
        ],
        dtype=np.float64,
    )


def average_quaternion(quats, weights=None):
    """四元数平均（Markley 法：加权 q·q^T 的最大特征向量）。quats: Nx4 [w,x,y,z]。"""
    Q = np.asarray(quats, dtype=np.float64)
    if weights is None:
        weights = np.ones(len(Q))
    weights = np.asarray(weights, dtype=np.float64)
    A = np.zeros((4, 4), dtype=np.float64)
    for qi, wi in zip(Q, weights):
        A += wi * np.outer(qi, qi)
    A /= weights.sum()
    eigvals, eigvecs = np.linalg.eigh(A)
    q = eigvecs[:, np.argmax(eigvals)]
    if q[0] < 0:
        q = -q
    return q / np.linalg.norm(q)


def quat_angle_deg(q1, q2):
    """两四元数代表旋转之间的测地角（度）。"""
    d = abs(float(np.dot(q1, q2)))
    d = min(1.0, max(-1.0, d))
    return np.degrees(2.0 * np.arccos(d))


def quat_xyzw_to_rotmat(qx, qy, qz, qw):
    """Unity 风格四元数 [x,y,z,w] → 3x3 旋转矩阵。"""
    return quat_to_rotmat(np.array([qw, qx, qy, qz], dtype=np.float64))


def ar_pose_to_T(ar_pose):
    """AR 世界位姿 {pos:[x,y,z], rot:[qx,qy,qz,qw]} → 4x4 T_world←arCam（Unity 左手系）。"""
    p = ar_pose["pos"]
    q = ar_pose["rot"]
    R = quat_xyzw_to_rotmat(q[0], q[1], q[2], q[3])
    return make_T(R, [p[0], p[1], p[2]])


def invT(T):
    R = T[:3, :3]
    t = T[:3, 3]
    Ti = np.eye(4)
    Ti[:3, :3] = R.T
    Ti[:3, 3] = -R.T @ t
    return Ti


# --------------------------------------------------------------------------- #
# ChArUco 检测 + 位姿求解（兼容新旧 OpenCV API）
# --------------------------------------------------------------------------- #

def get_aruco_dictionary(name):
    """按名称取 ArUco 预定义字典，兼容新旧 API。"""
    if not hasattr(aruco, name):
        raise ValueError(f"未知 ArUco 字典：{name}")
    dict_id = getattr(aruco, name)
    if hasattr(aruco, "getPredefinedDictionary"):
        return aruco.getPredefinedDictionary(dict_id)
    return aruco.Dictionary_get(dict_id)  # 旧 API


def _make_board(sx, sy, sq, mk, dictionary, legacy):
    """构建一个 ChArUco 板（新 API 优先，可设置 legacy 兼容老工具生成的板）。"""
    if hasattr(aruco, "CharucoBoard") and hasattr(aruco, "CharucoDetector"):
        board = aruco.CharucoBoard((sx, sy), sq, mk, dictionary)  # 新 API (>=4.7)
        if hasattr(board, "setLegacyPattern"):
            board.setLegacyPattern(legacy)
        return board, True
    # 旧 API (<=4.6)：本身即 legacy 排布
    board = aruco.CharucoBoard_create(sx, sy, sq, mk, dictionary)
    return board, False


def detect_board_config(board_info, probe_grays):
    """
    自动判别标定板配置：尺寸方向 (sx,sy)/(sy,sx) × legacy(True/False)。

    采集端写入的 squares_x/squares_y 与 OpenCV 构造顺序、以及板的生成方式（老工具
    用 legacy 排布）都可能不一致；这里在若干张样图上各候选配置实际检测一遍，取检出
    ChArUco 角点总数最多者，避免“尺寸/排布”不匹配导致全程检不出角点。
    """
    sx = int(board_info["squares_x"])
    sy = int(board_info["squares_y"])
    sq = float(board_info["square_len_m"])
    mk = float(board_info["marker_len_m"])
    name = board_info.get("dictionary", "DICT_4X4_50")
    dictionary = get_aruco_dictionary(name)

    candidates = []
    dim_options = [(sx, sy)] if sx == sy else [(sx, sy), (sy, sx)]
    for (dx, dy) in dim_options:
        for legacy in (True, False):
            candidates.append((dx, dy, legacy))

    best = None
    for (dx, dy, legacy) in candidates:
        board, new_api = _make_board(dx, dy, sq, mk, dictionary, legacy)
        solver = CharucoSolver(board, dictionary, new_api)  # 探测用默认参数
        total = 0
        for g in probe_grays:
            cc, ci = solver.detect_corners(g)
            total += 0 if ci is None else len(ci)
        log(f"  候选 dims=({dx},{dy}) legacy={legacy}: 探测角点合计={total}")
        if best is None or total > best[0]:
            best = (total, dx, dy, legacy, new_api)

    _, dx, dy, legacy, new_api = best
    log(f"采用标定板配置：OpenCV dims=({dx},{dy}), legacy={legacy}, dict={name}")
    return {
        "dx": dx, "dy": dy, "legacy": legacy, "sq": sq, "mk": mk,
        "dictionary": dictionary, "new_api": new_api,
    }


def build_solver(cfg, detector_params=None):
    """按已确定的标定板配置构建一个 CharucoSolver（可指定检测参数）。"""
    board, new_api = _make_board(
        cfg["dx"], cfg["dy"], cfg["sq"], cfg["mk"], cfg["dictionary"], cfg["legacy"]
    )
    return CharucoSolver(board, cfg["dictionary"], new_api, detector_params)


def make_tuned_detector_params():
    """放宽的 ArUco 检测参数：利于在小/糊/反光图（如手机 640x480 远景）里多检出标记。"""
    p = (
        aruco.DetectorParameters_create()
        if hasattr(aruco, "DetectorParameters_create")
        else aruco.DetectorParameters()
    )
    try:
        p.adaptiveThreshWinSizeMin = 3
        p.adaptiveThreshWinSizeMax = 53
        p.adaptiveThreshWinSizeStep = 4
        p.minMarkerPerimeterRate = 0.01
        p.maxMarkerPerimeterRate = 4.0
        p.polygonalApproxAccuracyRate = 0.05
        p.cornerRefinementMethod = aruco.CORNER_REFINE_SUBPIX
        p.cornerRefinementWinSize = 5
    except Exception:
        pass
    return p


class CharucoSolver:
    """封装一次构建、多次复用的 ChArUco 检测器。"""

    def __init__(self, board, dictionary, new_api, detector_params=None):
        self.board = board
        self.dictionary = dictionary
        self.new_api = new_api and hasattr(aruco, "CharucoDetector")
        self.detector_params = detector_params if detector_params is not None else (
            aruco.DetectorParameters_create()
            if hasattr(aruco, "DetectorParameters_create")
            else aruco.DetectorParameters()
        )
        if self.new_api:
            charuco_params = aruco.CharucoParameters()
            self.detector = aruco.CharucoDetector(board, charuco_params, self.detector_params)
        else:
            self.detector = None

    def detect_corners(self, gray):
        """返回 (charuco_corners Nx1x2, charuco_ids Nx1)；检测不到返回 (None, None)。"""
        if self.new_api:
            ch_corners, ch_ids, _, _ = self.detector.detectBoard(gray)
            if ch_ids is None or len(ch_ids) == 0:
                return None, None
            return ch_corners, ch_ids
        corners, ids, _ = aruco.detectMarkers(
            gray, self.dictionary, parameters=self.detector_params
        )
        if ids is None or len(ids) == 0:
            return None, None
        _, ch_corners, ch_ids = aruco.interpolateCornersCharuco(
            corners, ids, gray, self.board
        )
        if ch_ids is None or len(ch_ids) == 0:
            return None, None
        return ch_corners, ch_ids

    def solve_pose(self, gray, K, dist, upscales=(1,)):
        """
        检测 + solvePnP，返回 dict(ok, R, t, n_corners, reproj_err) 。
        upscales: 依次尝试的放大倍数（对小目标有帮助）；角点坐标会按倍数缩放回原图后再 solvePnP。
        """
        best = None
        for scale in upscales:
            g = gray
            if scale != 1:
                g = cv2.resize(gray, None, fx=scale, fy=scale, interpolation=cv2.INTER_CUBIC)
            ch_corners, ch_ids = self.detect_corners(g)
            if ch_ids is None:
                continue
            n = len(ch_ids)
            if best is None or n > best[2]:
                best = (ch_corners, ch_ids, n, scale)

        if best is None or best[2] < 6:
            return {"ok": False, "reason": "角点不足", "n_corners": 0 if best is None else best[2]}

        ch_corners, ch_ids, n, scale = best
        if scale != 1:
            ch_corners = ch_corners / float(scale)  # 缩放回原图像素坐标

        # 取 3D-2D 对应点
        if self.new_api and hasattr(self.board, "matchImagePoints"):
            obj_pts, img_pts = self.board.matchImagePoints(ch_corners, ch_ids)
        else:
            obj_all = _get_board_chessboard_corners(self.board)
            ids = ch_ids.reshape(-1)
            obj_pts = obj_all[ids].reshape(-1, 1, 3).astype(np.float64)
            img_pts = ch_corners.reshape(-1, 1, 2).astype(np.float64)

        if obj_pts is None or len(obj_pts) < 6:
            return {"ok": False, "reason": "匹配点不足", "n_corners": n}

        ok, rvec, tvec = cv2.solvePnP(
            obj_pts, img_pts, K, dist, flags=cv2.SOLVEPNP_ITERATIVE
        )
        if not ok:
            return {"ok": False, "reason": "solvePnP 失败", "n_corners": n}

        proj, _ = cv2.projectPoints(obj_pts, rvec, tvec, K, dist)
        err = float(np.sqrt(np.mean(np.sum((proj.reshape(-1, 2) - img_pts.reshape(-1, 2)) ** 2, axis=1))))

        R, _ = cv2.Rodrigues(rvec)
        return {
            "ok": True,
            "R": R,
            "t": tvec.reshape(3),
            "n_corners": int(n),
            "reproj_err": err,
        }


def _get_board_chessboard_corners(board):
    """旧 API 取板的棋盘格 3D 角点。"""
    if hasattr(board, "getChessboardCorners"):
        return np.asarray(board.getChessboardCorners(), dtype=np.float64)
    return np.asarray(board.chessboardCorners, dtype=np.float64)


# --------------------------------------------------------------------------- #
# 主流程
# --------------------------------------------------------------------------- #

# OpenCV(右手, Y下, Z前) → Unity(左手, Y上, Z前) 的轴翻转矩阵
S_FLIP_Y = np.diag([1.0, -1.0, 1.0]).astype(np.float64)


def flip_y_transform(T):
    """对 4x4 位姿做 Y 轴翻转 S·T·S（S=diag(1,-1,1)），用于 OpenCV(右手)↔Unity(左手) 互转。

    S 是对合矩阵（S·S=I），故同一函数可双向使用：
      - OpenCV 右手系 → Unity 左手系（最终外参转换）
      - Unity 左手系 → OpenCV 右手系（把 AR 位姿喂给手眼标定）
    """
    S4 = np.eye(4)
    S4[:3, :3] = S_FLIP_Y
    return S4 @ T @ S4


# 兼容旧命名
opencv_T_to_unity = flip_y_transform


def find_default_input(script_dir):
    candidates = sorted(
        glob.glob(os.path.join(script_dir, "input", "*.json")),
        key=os.path.getmtime,
        reverse=True,
    )
    return candidates[0] if candidates else None


def robust_average_T(quats, trans, max_rot_deg, max_trans_m):
    """对一组相对位姿（四元数 + 平移）做鲁棒平均（剔除离群）。返回 (T_cv, stats, inlier_idx)。"""
    quats = np.asarray(quats)
    trans = np.asarray(trans)
    n = len(quats)
    t_median = np.median(trans, axis=0)
    q_ref = average_quaternion(quats)

    inliers = []
    devs = []
    for i in range(n):
        ang = quat_angle_deg(quats[i], q_ref)
        dist_t = float(np.linalg.norm(trans[i] - t_median))
        devs.append((ang, dist_t))
        if ang <= max_rot_deg and dist_t <= max_trans_m:
            inliers.append(i)
    if len(inliers) < 3:
        inliers = list(range(n))

    q_avg = average_quaternion(quats[inliers])
    R_avg = quat_to_rotmat(q_avg)
    t_avg = np.median(trans[inliers], axis=0)

    angles = np.array([quat_angle_deg(q, q_avg) for q in quats[inliers]])
    rot_rms_deg = float(np.sqrt(np.mean(angles ** 2)))
    trans_sigma_mm = float(np.linalg.norm(trans[inliers].std(axis=0)) * 1000.0)

    stats = {
        "rot_consistency_deg": rot_rms_deg,
        "trans_consistency_mm": trans_sigma_mm,
        "num_inliers": len(inliers),
        "num_total": n,
        "devs": devs,
    }
    return make_T(R_avg, t_avg), stats, inliers


def solve_hand_eye_robust(Ps, Ms, max_iter=4, reject_trans_mm=15.0, reject_rot_deg=2.5):
    """
    手眼标定（AX=XB）求 X = T_arCam←orbbec（OpenCV 右手系）。

    Ps[i] = T_world←arCam（已转右手系），Ms[i] = T_orbbec←board（OpenCV 右手系）。
    板固定于世界：W_i = P_i · X · M_i = T_world←board 应为常量；用其离散程度做一致性
    评估与离群剔除，迭代重解。
    返回 (X_cv, stats, inlier_idx)。
    """
    method = getattr(cv2, "CALIB_HAND_EYE_PARK", 0)
    idx = list(range(len(Ps)))

    def solve(ix):
        R_g2b = [Ps[i][:3, :3] for i in ix]
        t_g2b = [Ps[i][:3, 3] for i in ix]
        R_t2c = [Ms[i][:3, :3] for i in ix]
        t_t2c = [Ms[i][:3, 3] for i in ix]
        R_x, t_x = cv2.calibrateHandEye(R_g2b, t_g2b, R_t2c, t_t2c, method=method)
        return make_T(R_x, t_x.reshape(3))

    X = solve(idx)
    for _ in range(max_iter):
        Ws = [Ps[i] @ X @ Ms[i] for i in idx]
        t_med = np.median(np.array([W[:3, 3] for W in Ws]), axis=0)
        q_ref = average_quaternion([rotmat_to_quat(W[:3, :3]) for W in Ws])
        new_idx = []
        for k, i in enumerate(idx):
            dt = float(np.linalg.norm(Ws[k][:3, 3] - t_med)) * 1000.0
            da = quat_angle_deg(rotmat_to_quat(Ws[k][:3, :3]), q_ref)
            if dt <= reject_trans_mm and da <= reject_rot_deg:
                new_idx.append(i)
        if len(new_idx) < 3 or len(new_idx) == len(idx):
            idx = new_idx if len(new_idx) >= 3 else idx
            break
        idx = new_idx
        X = solve(idx)

    # 最终一致性统计
    Ws = [Ps[i] @ X @ Ms[i] for i in idx]
    t_arr = np.array([W[:3, 3] for W in Ws])
    q_avg = average_quaternion([rotmat_to_quat(W[:3, :3]) for W in Ws])
    angs = np.array([quat_angle_deg(rotmat_to_quat(W[:3, :3]), q_avg) for W in Ws])
    stats = {
        "rot_consistency_deg": float(np.sqrt(np.mean(angs ** 2))),
        "trans_consistency_mm": float(np.linalg.norm(t_arr.std(axis=0)) * 1000.0),
        "num_inliers": len(idx),
        "num_total": len(Ps),
    }
    return X, stats, idx


def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    parser = argparse.ArgumentParser(
        description="阶段四：离线求解 T_arCam←orbbec 外参（ChArUco 目标板法）"
    )
    parser.add_argument(
        "--input", "-i", default=None,
        help="采集 JSON 文件路径；缺省读取脚本目录 input/ 下最新 *.json",
    )
    parser.add_argument(
        "--out-dir", "-o", default=os.path.join(script_dir, "output"),
        help="输出目录（默认 ./output）",
    )
    parser.add_argument(
        "--max-rot-deg", type=float, default=2.0,
        help="旋转离群剔除阈值：与中位旋转夹角超过该值的帧将被剔除（度，默认 2.0）",
    )
    parser.add_argument(
        "--max-trans-m", type=float, default=0.02,
        help="平移离群剔除阈值：与中位平移偏差超过该值的帧将被剔除（米，默认 0.02）",
    )
    parser.add_argument(
        "--min-corners", type=int, default=8,
        help="单帧两图各自最少 ChArUco 角点数（默认 8）",
    )
    parser.add_argument(
        "--debug", action="store_true",
        help="保存每帧角点检测可视化图到 output/debug/",
    )
    args = parser.parse_args()

    input_path = args.input or find_default_input(script_dir)
    if not input_path or not os.path.isfile(input_path):
        log("[错误] 未找到输入 JSON。请用 --input 指定，或把采集文件放到 "
            f"{os.path.join(script_dir, 'input')} 目录。")
        sys.exit(1)

    os.makedirs(args.out_dir, exist_ok=True)
    debug_dir = os.path.join(args.out_dir, "debug")
    if args.debug:
        os.makedirs(debug_dir, exist_ok=True)

    log(f"OpenCV 版本：{cv2.__version__}")
    log(f"读取采集文件：{input_path}")
    with open(input_path, "r", encoding="utf-8") as f:
        data = json.load(f)

    board_info = data["board"]
    samples = data["samples"]
    log(f"rig_version={data.get('rig_version')}  created_at={data.get('created_at')}")
    log(f"标定板：{board_info}")
    log(f"采集样本数：{len(samples)}")

    # 自动判别标定板配置（尺寸方向/legacy 排布），用前若干帧的两路图像做探测
    probe_grays = []
    for s in samples[: min(6, len(samples))]:
        for key in ("orbbec_image_jpg_base64", "phone_image_jpg_base64"):
            g = decode_jpg_base64_to_gray(s[key])
            if g is not None:
                probe_grays.append(g)
    log("自动探测标定板配置：")
    cfg = detect_board_config(board_info, probe_grays)
    # Orbbec 图大而清晰 → 默认参数检测更稳；手机图小/糊/反光 → 用放宽参数 + 放大
    solver_orbbec = build_solver(cfg, detector_params=None)
    solver_phone = build_solver(cfg, detector_params=make_tuned_detector_params())
    log(f"使用 {'新' if solver_orbbec.new_api else '旧'} 版 ChArUco API")

    dist_zero = np.zeros((5, 1), dtype=np.float64)  # 无畸变系数：AR 内参通常已校正
    phone_upscales = (1, 2, 3)  # 手机图小目标：放大重试以多检角点

    per_frame = []           # 每帧诊断
    # 目标板立体法（阶段四主方法）：两图都成功的帧
    stereo_quats, stereo_trans, stereo_idx = [], [], []
    # 手眼法（阶段七兜底）：Orbbec 检到板 + 有 AR 位姿的帧
    he_Ps, he_Ms, he_idx, he_reproj = [], [], [], []

    for s in samples:
        idx = s["index"]
        rec = {"index": idx}

        phone_gray = decode_jpg_base64_to_gray(s["phone_image_jpg_base64"])
        orbbec_gray = decode_jpg_base64_to_gray(s["orbbec_image_jpg_base64"])
        if phone_gray is None or orbbec_gray is None:
            rec["reason"] = "图像解码失败"
            per_frame.append(rec)
            continue

        K_phone = intr_to_K(s["phone_intr"])
        K_orbbec = intr_to_K(s["orbbec_intr"])

        rp = solver_phone.solve_pose(phone_gray, K_phone, dist_zero, upscales=phone_upscales)
        ro = solver_orbbec.solve_pose(orbbec_gray, K_orbbec, dist_zero)

        rec["phone_corners"] = rp.get("n_corners", 0)
        rec["orbbec_corners"] = ro.get("n_corners", 0)
        rec["phone_reproj"] = round(rp.get("reproj_err", -1), 4) if rp["ok"] else None
        rec["orbbec_reproj"] = round(ro.get("reproj_err", -1), 4) if ro["ok"] else None

        if args.debug:
            _save_debug(debug_dir, idx, "phone", phone_gray, solver_phone)
            _save_debug(debug_dir, idx, "orbbec", orbbec_gray, solver_orbbec)

        phone_ok = rp["ok"] and rp["n_corners"] >= args.min_corners
        orbbec_ok = ro["ok"] and ro["n_corners"] >= args.min_corners

        # 手眼法所需：Orbbec 看到板 + 该帧有 AR 世界位姿
        if orbbec_ok and s.get("ar_world_pose"):
            T_orbbec_board = make_T(ro["R"], ro["t"])              # T_orbbec←board (OpenCV 右手)
            P_unity = ar_pose_to_T(s["ar_world_pose"])             # T_world←arCam (Unity 左手)
            P_cv = flip_y_transform(P_unity)                       # 转右手系（S·P·S）
            he_Ps.append(P_cv)
            he_Ms.append(T_orbbec_board)
            he_idx.append(idx)
            he_reproj.append(ro["reproj_err"])
            rec["used_handeye"] = True

        # 目标板立体法所需：两图都检到板
        if phone_ok and orbbec_ok:
            T_phone_board = make_T(rp["R"], rp["t"])
            T_orbbec_board = make_T(ro["R"], ro["t"])
            T_phone_orbbec = T_phone_board @ np.linalg.inv(T_orbbec_board)
            stereo_quats.append(rotmat_to_quat(T_phone_orbbec[:3, :3]))
            stereo_trans.append(T_phone_orbbec[:3, 3].copy())
            stereo_idx.append(idx)
            rec["used_stereo"] = True

        if not orbbec_ok:
            rec["reason"] = f"Orbbec 角点不足({ro.get('n_corners',0)})"
        elif not phone_ok:
            rec["reason"] = f"手机角点不足({rp.get('n_corners',0)})，仅用于手眼法"
        per_frame.append(rec)

    log(f"\n目标板立体法可用帧（两图均检到板）：{len(stereo_idx)}  -> {stereo_idx}")
    log(f"手眼法可用帧（Orbbec 检到板 + AR 位姿）：{len(he_idx)}  -> {he_idx}")

    # ---- 选择方法：立体法帧足够则用立体法，否则用手眼法兜底 ----
    MIN_STEREO = 8
    result = None  # (method, T_unity, stats, used_indices, reproj)

    if len(stereo_idx) >= MIN_STEREO:
        log(f"\n采用【目标板立体法】（阶段四主方法），可用帧 {len(stereo_idx)} ≥ {MIN_STEREO}")
        T_cv, stats, inl = robust_average_T(
            stereo_quats, stereo_trans, args.max_rot_deg, args.max_trans_m
        )
        T_unity = opencv_T_to_unity(T_cv)
        used = [stereo_idx[i] for i in inl]
        rv = [per_frame_lookup(per_frame, j, "orbbec_reproj") for j in used]
        rv = [x for x in rv if x is not None]
        result = ("target_board_stereo", T_unity, stats, used, float(np.mean(rv)) if rv else -1.0)
    elif len(he_idx) >= 3:
        log(f"\n手机图可用帧不足（{len(stereo_idx)} < {MIN_STEREO}，手机远景小/糊/反光），"
            f"自动改用【手眼法 AX=XB】（阶段七兜底），可用帧 {len(he_idx)}")
        X_cv, stats, inl = solve_hand_eye_robust(he_Ps, he_Ms)
        T_unity = opencv_T_to_unity(X_cv)
        used = [he_idx[i] for i in inl]
        rv = [he_reproj[i] for i in inl]
        for j in used:
            for rec in per_frame:
                if rec["index"] == j:
                    rec["handeye_inlier"] = True
        result = ("hand_eye_AXXB", T_unity, stats, used, float(np.mean(rv)) if rv else -1.0)
    else:
        log("[错误] 有效帧过少：目标板立体法与手眼法均无法求解。"
            "请检查标定板是否清晰、Orbbec 是否出图、AR 位姿是否记录。")
        _dump_report(args.out_dir, input_path, data, per_frame, None, partial=True)
        sys.exit(1)

    method, T_unity, stats, used, mean_reproj = result

    # ---- 打印结果 ----
    log("\n================ 求解结果 ================")
    log(f"方法：{method}")
    log("T_arCam←orbbec (Unity 左手系, 单位米)：")
    for r in range(4):
        log("  [{: .6f} {: .6f} {: .6f} {: .6f}]".format(*T_unity[r]))
    tx, ty, tz = T_unity[:3, 3]
    log(f"平移 (Unity): x={tx:.4f}  y={ty:.4f}  z={tz:.4f}  m  "
        f"(|t|={np.linalg.norm([tx,ty,tz])*100:.2f} cm)")
    log("\n一致性 / 误差：")
    log(f"  使用帧数：{stats['num_inliers']} / {stats['num_total']}")
    log(f"  旋转一致性 RMS：{stats['rot_consistency_deg']:.3f}°   (期望 <0.5°)")
    log(f"  平移一致性 σ：{stats['trans_consistency_mm']:.2f} mm  (期望 几 mm 级)")
    log(f"  Orbbec 板重投影误差：{mean_reproj:.3f} px  (期望 <1 px)")

    quality = []
    quality.append("重投影 OK" if 0 <= mean_reproj < 1.0 else "重投影偏大")
    quality.append("旋转一致 OK" if stats["rot_consistency_deg"] < 0.5 else "旋转一致性偏差大")
    quality.append("平移一致 OK" if stats["trans_consistency_mm"] < 5.0 else "平移一致性偏差大")
    log("  评估：" + "，".join(quality))

    # 若两种方法都可用，交叉对比一致性，便于发现系统误差
    if method == "target_board_stereo" and len(he_idx) >= 3:
        X_cv, he_stats, _ = solve_hand_eye_robust(he_Ps, he_Ms)
        T_he = opencv_T_to_unity(X_cv)
        d_t = float(np.linalg.norm(T_he[:3, 3] - T_unity[:3, 3]) * 1000.0)
        d_r = quat_angle_deg(rotmat_to_quat(T_he[:3, :3]), rotmat_to_quat(T_unity[:3, :3]))
        log(f"\n交叉校验（手眼法 vs 立体法）：平移差 {d_t:.1f} mm, 旋转差 {d_r:.2f}°")

    # ---- 4.4 导出 ----
    extr_path = os.path.join(args.out_dir, "orbbec_ar_extrinsic.json")
    extr = {
        "T_arCam_from_orbbec": [round(float(v), 8) for v in T_unity.reshape(-1)],
        "_layout": "row-major 4x4 (r00,r01,r02,tx, r10,r11,r12,ty, r20,r21,r22,tz, 0,0,0,1)",
        "_coordinate_system": "Unity 左手系, +Y上 +Z前, 单位米",
        "method": method,
        "reproj_error_px": round(mean_reproj, 4),
        "rot_consistency_deg": round(stats["rot_consistency_deg"], 4),
        "trans_consistency_mm": round(stats["trans_consistency_mm"], 4),
        "num_used_frames": stats["num_inliers"],
        "num_total_frames": len(samples),
        "used_frame_indices": used,
        "calibrated_at": datetime.date.today().isoformat(),
        "rig_version": data.get("rig_version", "unknown"),
        "source_file": os.path.basename(input_path),
    }
    with open(extr_path, "w", encoding="utf-8") as f:
        json.dump(extr, f, indent=2, ensure_ascii=False)
    log(f"\n外参已导出：{extr_path}")
    log("  → 复制到 Unity：Assets/StreamingAssets/orbbec_ar_extrinsic.json")

    _dump_report(args.out_dir, input_path, data, per_frame, extr)


def per_frame_lookup(per_frame, index, key):
    for rec in per_frame:
        if rec["index"] == index:
            return rec.get(key)
    return None


def _save_debug(debug_dir, idx, tag, gray, solver):
    try:
        ch_corners, ch_ids = solver.detect_corners(gray)
        vis = cv2.cvtColor(gray, cv2.COLOR_GRAY2BGR)
        if ch_ids is not None and len(ch_ids) > 0:
            aruco.drawDetectedCornersCharuco(vis, ch_corners, ch_ids)
        cv2.imwrite(os.path.join(debug_dir, f"frame{idx:02d}_{tag}.jpg"), vis)
    except Exception as e:  # pragma: no cover
        log(f"[debug] 保存 frame{idx} {tag} 失败：{e}")


def _dump_report(out_dir, input_path, data, per_frame, extr, partial=False):
    report = {
        "source_file": os.path.basename(input_path),
        "created_at": data.get("created_at"),
        "rig_version": data.get("rig_version"),
        "board": data.get("board"),
        "result": extr,
        "partial": partial,
        "per_frame": per_frame,
    }
    path = os.path.join(out_dir, "calib_report.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2, ensure_ascii=False)
    log(f"逐帧诊断报告：{path}")


if __name__ == "__main__":
    main()
