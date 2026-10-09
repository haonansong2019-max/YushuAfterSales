# 钰叔售后在线服务契约

不得复用其他产品的授权 AppID、授权状态、更新路由或稳定清单。部署前必须先给 `ysrepair` 创建独立路由和产品记录。此仓库不保存服务器凭据。

## 更新清单

配置 `updates.manifestUrl` 为产品专用 HTTPS JSON 地址。字段为 `appId` (`ysrepair`)、`version`、`downloadUrl`（HTTPS）、`sizeBytes`、`sha256`（64 位十六进制）、`notes`。客户端校验产品 ID、绝对 HTTPS 地址、大小、版本和哈希；下载后先写临时文件并验证 SHA-256，再交给独立升级器。客户端不会用“仅 HTTPS”代替包完整性校验。

## 授权 API

配置 `authorization.baseUrl` 为产品专用 HTTPS API 根地址；用户主动提交卡密或点击刷新时，客户端调用 `api/auth/addUser`、`api/auth/useAuthCode`、`api/auth/getUserExpire`。表单包含 `appid=ysrepair`、机器码和（仅激活时）卡密；客户端不自动发送授权请求。

授权回执和离线有效期需保存于 DPAPI CurrentUser 加密文件与 HKCU 镜像，绑定稳定的机器指纹并阻止系统时钟回拨；离线宽限最多 7 天。未配置地址或服务不可用时，授权状态必须为无效/离线过期，修复/安装/升级禁用；扫描与报告仍可用。不得有可被调用的“本地伪造在线成功”入口。

## 上线检查

运维方必须单独验证授权服务识别 `ysrepair`、卡密归属、机器绑定和到期状态；更新服务必须拒绝其他产品 manifest 并只提供产品专属产物。此契约文件不构成已部署或已验证服务的证据。
