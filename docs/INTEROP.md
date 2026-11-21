# 机器人视觉沙盘本机消息协议 v1

此协议为单向观察协议。发送端不引用沙盘程序集，接收端不引用平台程序集。任意实现同一协议的程序均可发送遥测。

## 传输

- Windows 命名管道：`RobotVisionSortingTwin.v1`，完整名称为 `\\.\pipe\RobotVisionSortingTwin.v1`。
- 沙盘为服务端，只读；平台为客户端，只写。
- UTF-8，无 BOM，每个 JSON 对象以 LF 换行结尾；消息可跨多次写入，也可一次写入多条。
- JSON 属性名区分大小写。单条最多 262144 字符；JSON 深度最多 16。
- 服务端使用 `PipeOptions.CurrentUserOnly`，只允许当前 Windows 用户连接。不监听 TCP 端口。
- 同时运行一个沙盘观察端和一个平台发布端。断线后重新连接，接收端不要求两程序按顺序启动。

## 会话 / 心跳

发送端连接成功后立即发送，随后约每秒发送一次。`Products` 是当前产品库目录，最多 128 项，仅含 GUID 和名称。

```json
{
    "Schema": "robot-vision-twin/v1",
    "Type": "session",
    "Running": true,
    "Time": "2026-11-04T12:00:00Z",
    "Products": [
        {
            "Id": "8fb3298c-41c7-4428-a92c-4bd1ce46c898",
            "Name": "轴套 A"
        }
    ]
}
```

`Running` 为平台自动识别任务是否运行，与机器人连接状态分开。停止后仍发送 `Running: false` 心跳。源进程退出则管道关闭；4 秒未收到消息也会被沙盘视为失去实时状态。

## 识别采样

```json
{
    "Schema": "robot-vision-twin/v1",
    "Type": "sample",
    "Running": true,
    "RunId": "b32d8042-99e0-4965-b85c-598a057384b4",
    "Sequence": 1,
    "Time": "2026-11-04T12:00:01Z",
    "Status": "Recognized",
    "ProductId": "8fb3298c-41c7-4428-a92c-4bd1ce46c898",
    "ProductName": "轴套 A",
    "Area": 12400.5,
    "Perimeter": 433.2,
    "Sent": true,
    "ElapsedMs": 12.5,
    "ImageJpeg": null
}
```

| 字段 | 语义 |
|---|---|
| `RunId` | 每次自动识别会话的唯一 GUID。变化时重置沙盘现场及统计。 |
| `Sequence` | 会话内从 1 递增的采样序号。同一会话的旧序号及重复序号不生成工件。 |
| `Status` | `Recognized`、`NoMatch`、`Ambiguous`、`InvalidTarget`、`ResolutionMismatch`。 |
| `ProductId` / `ProductName` | 唯一识别成功时必填。沙盘按 GUID 查询用户选择的目标料仓。 |
| `Area` / `Perimeter` | 原平台测量值，可为 null，单位分别为 px² 与 px。 |
| `Sent` | 产品类型已成功写入机器人 TCP 流；不代表机器人已执行。 |
| `ElapsedMs` | 当前采样分析与记录耗时，非机器人动作时间。 |
| `ImageJpeg` | 可选 JPEG 的 Base64 字符串。配套发布器将最长边缩至 320 px，质量 72，最多 100000 字节。 |

无唯一识别结果、产品没有指定料仓或产品重复映射时，沙盘把该展示工件送往复检。配套发送器的预览来自平台的识别标注图，不是沙盘二次识别的结果。

## 流量与失败

配套发布器由独立后台任务编码和发送，采样回调仅向最多 16 项的有界队列投递；队列满时淘汰最旧消息。写入超时 1 秒，连接失败后重试。重新连接时清除历史积压，先发送当前目录和运行状态，再发送新采样。

沙盘显示序号缺口以暴露缺失消息，最多接受 64 件在途展示工件；输送带入口按间距逐件放行，待进入输送带的消息不画到厂房之外。超过容量的采样仍更新视觉预览，并累计动画队列溢出。

此协议不支持命令、设备启停、机器人 ACK、关节反馈、坐标标定或实体工件去重。相应能力可在未来版本中增加明确的新消息类型，不应复用 `Sent` 或 `Sequence` 冒充执行确认和实体身份。
