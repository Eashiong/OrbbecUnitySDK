# 阶段四：离线求解外参（PC 端 Python / OpenCV）

对应 `相机标定.md` 的「阶段四」。输入阶段三采集工具（`Assets/AR/CalibrationCapture.cs`）落盘的
单个 JSON，离线求解 **Orbbec 彩色相机 → 手机 AR 相机** 的常量外参
`T_arCam←orbbec`（Unity 左手系，单位米），并导出供阶段五运行时融合使用。

```
P_world = T_world←arCam(t) · T_arCam←orbbec · P_orbbec
                                ^^^^^^^^^^^^^^ 本阶段交付物
```

---

## 1. 环境与安装

需要 Python 3.10+（已在 3.12.10 验证）。

```bash
cd OrbbecUnitySDK/Calibration
pip install -r requirements.txt
```

> `aruco` 模块在 `opencv-contrib-python` 里，**不要**同时安装 `opencv-python`，否则可能冲突。

## 2. 运行

把阶段三采集（并上传）的 JSON 放到 `Calibration/input/`，然后：

```bash
python solve_extrinsic.py                 # 自动取 input/ 下最新的 *.json
python solve_extrinsic.py -i 路径/xxx.json # 指定输入
python solve_extrinsic.py --debug          # 额外把角点检测可视化图存到 output/debug/
```

输出（`Calibration/output/`）：

- `orbbec_ar_extrinsic.json` —— **最终外参**（行主序 4×4 + 误差报告）。
- `calib_report.json` —— 逐帧诊断（每帧两路图各检出多少 ChArUco 角点、重投影误差、是否被采用、离群偏差）。

把 `orbbec_ar_extrinsic.json` 复制到 Unity 工程的
`Assets/StreamingAssets/orbbec_ar_extrinsic.json`，供阶段五加载。

## 3. 处理流程

1. **自动判别标定板配置**：采集端记录的 `squares_x/squares_y` 与 OpenCV 构造顺序、以及板的
   生成方式（老工具用 *legacy* 标记排布）可能不一致。脚本会在前几帧的两路图像上，对
   `(sx,sy)/(sy,sx) × legacy(开/关)` 各候选实际检测一遍，选检出角点最多的配置，避免“方向/排布”
   不匹配导致全程检不出角点。
   > 本数据集自动选中 **OpenCV dims=(11,8), legacy=True, DICT_4X4_50**（采集端写的是 8×11，
   > 方向与 OpenCV 相反，且板是 legacy 排布——这是最常见的两个坑）。
2. **单帧求板位姿**（4.1）：每帧两图各做 ChArUco 检测 + `solvePnP`，得到
   `T_phone←board` / `T_orbbec←board`（OpenCV 右手系）。Orbbec 图大而清晰用默认检测参数；
   手机图小/糊/反光用放宽参数并尝试 2×/3× 放大以多检角点。
3. **求相对外参**：
   - 目标板立体法（阶段四主方法）：`T_phone←orbbec = T_phone←board · inv(T_orbbec←board)`。
   - 手眼法 AX=XB（阶段七兜底）：用每帧 `T_world←arCam`（AR 位姿）+ `T_orbbec←board`，
     令 `T_world←board = P·X·M` 为常量，`cv2.calibrateHandEye` 解出 `X = T_arCam←orbbec`。
4. **多帧鲁棒融合**（4.2）：平移取中位数、旋转取四元数特征向量平均，并按几何角度/平移偏差
   剔除离群帧；输出旋转/平移一致性与重投影误差。
5. **坐标系转换**（4.3）：OpenCV(右手,Y下) → Unity(左手,Y上)，`T_unity = S·T_opencv·S`，
   `S = diag(1,-1,1)`。手眼法里也先把 AR 位姿翻转到右手系再解算，最后统一翻回 Unity。
6. **导出**（4.4）：写 `orbbec_ar_extrinsic.json`（行主序 16 元素）+ 误差/一致性报告。

### 方法自动选择

- 手机图与 Orbbec 图**同时**检到板的帧数 ≥ 8 → 用**目标板立体法**（精度更高，且会附带与手眼法的交叉校验）。
- 否则 → 自动改用**手眼法**兜底（只需 Orbbec 看到板 + 有 AR 位姿）。

## 4. 输出字段说明（`orbbec_ar_extrinsic.json`）

| 字段 | 含义 |
|------|------|
| `T_arCam_from_orbbec` | 行主序 4×4：`[r00,r01,r02,tx, r10,r11,r12,ty, r20,r21,r22,tz, 0,0,0,1]`，Unity 左手系，米 |
| `method` | `target_board_stereo`（立体法）或 `hand_eye_AXXB`（手眼法） |
| `reproj_error_px` | Orbbec 板平均重投影误差（期望 <1px） |
| `rot_consistency_deg` | 多帧旋转一致性 RMS（期望 <0.5°） |
| `trans_consistency_mm` | 多帧平移一致性 σ（期望 几 mm 级） |
| `num_used_frames` / `num_total_frames` | 实际采用 / 采集总帧数 |
| `used_frame_indices` | 采用的帧序号 |

## 5. 当前数据（`06_17_14_56_36.json`）结果与说明

- 26 帧采集，自动选用**手眼法**：因为**手机相机图里标定板太小/反光/偏糊**，26 帧里手机几乎检不到
  完整 ChArUco（仅 1~3 帧勉强够），不足以做立体法；而 Orbbec 14 帧能稳定检到板。
- 结果：`|t| ≈ 12 cm`、旋转接近单位阵（两相机大致同向），形态与“手机 + Orbbec 固连支架”物理吻合。
- 一致性偏大（旋转 RMS ≈ 0.79°、平移 σ ≈ 1.0 cm）。这是**手眼法 + AR 位姿噪声/漂移**的固有上限，
  作为阶段五“静止叠合”的初版外参可用，但要拿到 <0.5°/几 mm 级精度，建议：

> **如何显著提升精度**（改进采集，对应阶段三）：
> 1. 让**手机相机也清晰看到整块标定板**（板更大、离手机更近、避免反光/玻璃、不过曝、保持静止），
>    使脚本能走更准的**目标板立体法**。
> 2. 标定板**正对、铺满更大画面**，覆盖近/中/远与多角度，采 15~30 组。
> 3. 确认支架**完全刚性**、标定全程不松动。

## 6. 常见问题

- **全程检不出角点**：多半是标定板“方向/legacy 排布”不匹配——脚本已自动尝试 4 种组合并打印探测结果，
  看日志“采用标定板配置”一行确认。
- **`No module named cv2`**：未装依赖或装成了 `opencv-python`（无 aruco），改装 `opencv-contrib-python`。
- **手眼法一致性差**：增加帧数与旋转多样性，或改善手机端板检测以切换到立体法。

## 7. 接阶段五

在 Unity 启动时读取 `Assets/StreamingAssets/orbbec_ar_extrinsic.json`，按 `_layout`（行主序）
填 `Matrix4x4`，运行时对每个 Orbbec 点：

```csharp
Matrix4x4 worldFromArCam   = arCamera.transform.localToWorldMatrix;
Matrix4x4 worldFromOrbbec  = worldFromArCam * T_arCam_from_orbbec;
Vector3   pWorld           = worldFromOrbbec.MultiplyPoint3x4(pOrbbec);
```
