# SGSR 2-pass (Unity URP 17 / RenderGraph)

实现对应用户提供的 `sgsr2_convert.fs` 和 `sgsr2_upscale.fs`，保留官方五点快速重建路径。
官方参数来源：https://github.com/SnapdragonGameStudios/adreno-gpu-vulkan-code-sample-framework/blob/main/samples/sgsr2/code/main/sgsr2_context_frag.cpp

## 渲染流程

在 AddRenderPasses 中、URP 创建相机附件之前，将场景渲染描述符改为原生输出尺寸乘 SGSR Render Scale。颜色、深度、motion vector 直接在该分辨率生成；jitter 按该分辨率的像素计算。

1. **SGSR Convert**：读取低分辨率 cameraDepthTexture 和 motionVectorColor，输出临时 RGBA16F MotionDepthClip（RG = Unity UV motion，B = depth clip，A = 0）。不再缩小或复制颜色。
2. **SGSR Upscale**：使用当前 jitter、五点 FastLanczos、加权颜色方差包围盒和历史裁剪，直接读取低分辨率场景颜色，重建到原生输出大小，直接写入下一帧的历史纹理。
3. **SGSR Present**：URP 内置 blit 将历史输出复制到独立的全分辨率颜色纹理，再将其设置为 resources.cameraColor。随后恢复 cameraTargetDescriptor、renderScale 和屏幕尺寸常量，后续后处理与最终显示使用全分辨率；不会再写回低分辨率目标。这只是集成所需的复制，不是第三段 SGSR 算法；shader 本身只有 Convert、Upscale 两个 pass。

执行时机固定在 BeforeRenderingPostProcessing，保证深度、相机/物体速度和透明物体颜色已生成。旧的调试模式和固定 historyBlend 已替换为官方自适应混合。

## Unity 与官方输入差异

- Unity motion 是有符号的 `currentUV - previousUV`，直接采样，包括零值和负值，不执行 decodeVelocityFromTexture，也不按 motion.x 的正负判断有效性。
- 重投影为 `previousUV = outputUV - motion`。无需再乘 0.5、翻转 motion.y 或减去 jitter delta。官方以 NDC motion 调节滤波的地方仍使用 `2 * UnityMotion`，保持阈值和速度量级一致。
- Reversed Z 先转换成 near=0、far=1 的原始非线性深度，再执行原版 min/depthsep 公式。
- 输入尺寸与深度纹理一致时使用四次原生 gather；深度尺寸不一致时用同等排列的十六次点采样，保证深度邻域和低分辨率颜色网格一致。
- `scaleRatio = (outputWidth / inputWidth, min(20, (outputArea / inputArea)^3))`；`cameraFovAngleHor` 为水平半 FOV 的正切。相机静止超过五帧时才启用 minLerpContribution（默认 0.3）。
- 保留输入 alpha；官方原版输出 alpha 为零。屏幕边缘读取会 clamp；历史越界和首帧跳过历史采样；仅在 Lanczos 权重和的绝对值接近零时回退到当前颜色，保留官方负权重。

## 使用

现有 PC_Renderer 的 SGSR feature 和 M_SGSR 材质已接入。Game 相机挂载并启用 CameraJitter，开启 Enable History、Enable Jitter，Jitter Scale 使用 1。Render Scale 为 1 时测试同分辨率时域重建，0.5 时测试两倍放大。不要同时开启 URP TAA/STP，否则会重复进行时域处理。

**SGSR Render Scale 现在控制实际场景分辨率。** 例如 2560×1440 输出、SGSR Render Scale=0.5 时，场景颜色/深度/速度为 1280×720，历史与输出为 2560×1440。URP Asset 的 Render Scale 保持 1、Upscaling Filter 使用 Automatic/Bilinear/Nearest-Neighbor，并关闭 TAA/STP/FSR 与硬件动态分辨率，避免叠加另一套升频。当前 PC_RPAsset 满足这些前提；不兼容配置会跳过 SGSR 并提示。后处理在重建后运行，所以这部分仍为全分辨率，性能收益需实测。

历史按相机隔离。首次使用、分辨率或 jitter 设置变化、投影变化、跳帧、明显位移/旋转会重置历史。脚本切镜也可调用 `SGSR.ResetHistory(camera)`，无参数则重置所有相机。Feature 释放和已销毁/长期未使用相机的历史纹理会清理。

集成范围：普通非 XR、无 Overlay 堆叠的 Base Game 相机、RenderGraph、固定目标尺寸。Scene View、Preview、Reflection、Overlay、XR 相机跳过；没有实现 Compatibility Mode 或硬件动态分辨率适配。透明材质若不写 motion vectors，仍存在时域拖影的输入限制。

## 验证

已用项目安装的 Unity Roslyn 编译 C#，并用 D3DCompiler 对项目 URP 头文件下的两个 pass 的 vertex/fragment 入口进行离线 D3D11 编译。离线检查替代了 Unity 编译器提供的 half 类型宏，未改动项目包文件。另已在 Unity 6000.3.11f1 / DX12 的 DynamicScene 中运行 Play Mode：Render Scale 从 0.599 切到 0.5，实际颜色/深度/速度纹理均从 1533×863 变为 1280×720，输出/历史保持 2560×1440；两块板、旋转方块和运动球体均可见，Console 为 0 warning、0 error。未验证移动平台、其他后处理组合或性能。

运行检查：静止收敛、相机平移、动态物体遮挡、屏幕边缘、窗口缩放、开关历史与 jitter、切换相机。Frame Debugger 应看到上述两段 SGSR 算法和一个 Present blit；Console 不应出现纹理读写冲突或 shader 错误。

编辑器中首次渲染或改变尺寸时，Console 会输出实际颜色、深度、速度和输出/历史的尺寸，便于确认没有重复缩放。



## 历史融合修正（2026-09-14）

两个 pass 原先在记录绘制命令时修改同一个 Material。命令持有材质引用，Upscale 的默认 FOV=0 会覆盖 Convert 的 FOV，导致平面上的 depth clip 错误地变为 1，历史积累失效。现改用 MaterialPropertyBlock + DrawProcedural，为每个 draw 快照纹理和常量，避免跨 pass、跨相机串值。另移除了对负 Lanczos 权重和的双线性回退，保持官方滤波权重。

在 DX12 的真实 RenderGraph 中临时加入有纹理依赖的 GPU readback，读取输入、MotionDepthClip 和历史输出，验证后移除诊断代码。相同静止视角、Render Scale=0.5、1280×720 → 2560×1440、History/Jitter 开启、8 相位：修复前连续 8 帧的 depth clip 中位数均为 1；修复后均为 0，90% 分位约 0.04。两组输出在线性 RGB 下逐像素计算 8 帧标准差再取平均，结果从 0.01888 降至 0.002144（约减少 89%）。这是该静止视角的采样结果，预热帧数不同，不作为通用质量或性能基准。

Play Mode 下另读取连续 8 帧，确认运动向量非零、历史没有逐帧重置，运动球体和旋转方块正常显示；Console 无 warning/error。C# 和两个 shader pass 的离线编译通过。棋盘和细线材质使用未预过滤的 floor/step 高频图案，远处仍可出现摩尔纹；SGSR 的时域融合不能保证完全消除超过采样能力的细节混叠。

## 不透明物体拖影修正记录（深度硬拒绝已撤回）

2026-09-21：下述 eye-depth 硬拒绝已撤回，相关 metadata 双缓冲、矩阵、上一帧 jitter 参数和 History Depth Threshold 均已移除。Convert 当前使用单张临时 RGBA16F 纹理，A=0，不再记录 eye depth。YCoCg 裁剪、motion 单位转换及原有历史重置逻辑保留。此次仅做文件修改及静态检查，未操作 Unity、未编译或运行验证。以下内容保留为之前的改动记录。

本次在官方 2-pass-FS 基础上增加历史拒绝，仍为 Convert + Upscale 两个算法 pass：

- Convert 的 A 通道保存当前线性 eye depth，metadata 改为 RGBA32F 双缓冲，与颜色历史使用相同的交换、重置和释放时机。1280×720 时两张 metadata 纹理合计约 28.1 MiB；这是质量优先的实现，尚未测量带宽和性能。
- Upscale 用 `previousUV + previousJitter / renderSize` 查上一帧的 metadata。当前深度通过当前 jittered 逆 VP 重建位置，再转换到上一帧相机空间比较 eye depth，避免直接比较两个相机空间的深度。超出容差或屏幕边界时只用当前帧重建颜色。
- 新增 `History Depth Threshold`，默认相对容差 0.02，另有 0.01 世界单位的绝对下限。UV motion 不含物体沿深度方向的位移，因此此类运动可能保守地拒绝历史，代价是稳定性下降；这不是完整的物体上一帧位置重建。
- 五点滤波及历史颜色裁剪改为线性的 YCoCg，最终转回 RGB；历史色度超出当前范围时不使用静止相机的历史放宽参数，避免高反差黑白邻域保留旧粉色。

小球 Renderer 的 `m_MotionVectors: 1` 对应 Object，不能仅因材质序列化了禁用 MotionVectors 就认定缺失物体速度。本次没有改写 Unity motion，也没有启用速度解码。透明层没有独立深度，此次改动不构成透明重建支持。

按用户要求，本次只修改文件，未操作 Unity、未编译或运行验证。上面的历史验证结果属于本次改动之前，帧间波动减少也不等同于拖影改善；本次效果仍需运行观察。
