# 如何获取 Gmail Client ID

为了让 Keepraw Mail 能够访问您的 Gmail 邮件，您需要创建一个 Google Cloud 项目并获取 OAuth 2.0 凭据。

## 步骤 1：创建 Google Cloud 项目

1.  访问 [Google Cloud Console](https://console.cloud.google.com/)。
2.  点击左上角的项目下拉菜单，选择 **"新建项目"**。
3.  输入项目名称（例如 "Keepraw Mail"），点击 **"创建"**。

## 步骤 2：启用 Gmail API

1.  在左侧菜单中，点击 **"API 和服务"** > **"库"**。
2.  在搜索框中输入 "Gmail API"。
3.  点击搜索结果中的 **"Gmail API"**。
4.  点击 **"启用"**。

## 步骤 3：配置 OAuth 同意屏幕

1.  在左侧菜单中，点击 **"API 和服务"** > **"OAuth 同意屏幕"**。
2.  选择 **"外部"** (External)，点击 **"创建"**。
3.  填写应用信息：
    *   **应用名称**：Keepraw Mail
    *   **用户支持电子邮件**：您的 Gmail 地址
    *   **开发者联系信息**：您的 Gmail 地址
4.  点击 **"保存并继续"**。
5.  在 **"范围"** (Scopes) 页面，点击 **"添加或移除范围"**。
6.  搜索并勾选 `https://mail.google.com/` (Gmail API)，点击 **"更新"**。
7.  点击 **"保存并继续"**。
8.  在 **"测试用户"** (Test users) 页面，点击 **"添加用户"**，输入您自己的 Gmail 地址。（这一步至关重要，否则您无法登录）。
9.  点击 **"保存并继续"**。

## 步骤 4：创建凭据 (Client ID)

1.  在左侧菜单中，点击 **"API 和服务"** > **"凭据"**。
2.  点击顶部的 **"创建凭据"** > **"OAuth 客户端 ID"**。
3.  **应用类型**选择 **"桌面应用"** (Desktop app)。
4.  **名称**输入 "Keepraw Client"。
5.  点击 **"创建"**。
6.  您将看到弹出的 **"您的客户端 ID"** 和 **"您的客户端密钥"**。
    *   复制 **客户端 ID** (Client ID)。
    *   客户端密钥 (Client Secret) 对于桌面应用不是必须保密的，但在 Keepraw Mail 中我们主要使用 Client ID。

## 步骤 5：在 Keepraw Mail 中配置

**凭据不会写进源码，也不会提交到版本库**（否则会触发 GitHub Secret Scanning 拦截推送）。
请用下面任意一种方式配置：

### 方式一：本地凭据文件（推荐）

创建文件 `%LOCALAPPDATA%\KeeprawMail\oauth.credentials.json`：

```json
{
  "clientId": "你的客户端 ID.apps.googleusercontent.com",
  "clientSecret": "你的客户端密钥"
}
```

此文件位于仓库之外，不会被 git 跟踪。

### 方式二：环境变量

```powershell
$env:KEEPRAWMAIL_GOOGLE_CLIENT_ID = "你的客户端 ID.apps.googleusercontent.com"
$env:KEEPRAWMAIL_GOOGLE_CLIENT_SECRET = "你的客户端密钥"
```

环境变量优先级高于凭据文件，适合 CI / 打包流水线。

### 关于客户端密钥

Google 为“桌面应用”类型签发的 client secret 并不是真正的机密（任何分发出去的桌面程序都能被提取），
本应用已使用 PKCE(S256) 防止授权码被截获后冒用。但 Google 的 token 端点对 Desktop 客户端
**通常仍要求**携带 `client_secret`，因此建议照常填写；若留空，程序不会发送该参数。

配置完成后，重新编译并运行 Keepraw Mail，即可使用 "Sign in with Google" 登录。

## 授权要求与“此应用已被阻止”

`https://mail.google.com/`（IMAP/SMTP 完整访问）是 Google 定义的 **受限（restricted）范围**。
这意味着：

| 应用发布状态 | 谁能登录 | Refresh Token 有效期 | 需要做什么 |
|---|---|---|---|
| **测试（Testing）** | 只有 OAuth 同意屏幕中登记的**测试用户**（最多 100 个） | **7 天**后失效，需要重新登录 | 在“测试用户”中添加自己的邮箱 |
| **正式（In production）· 仅个人使用** | 任何人，但会看到“Google 尚未验证此应用”警告，且用户数有上限 | 正常 | 自用可点击“高级 → 继续” |
| **正式 · 面向公众分发** | 任何人 | 正常 | 必须通过 Google 的应用验证（品牌、隐私政策、域名所有权）以及受限范围所需的**第三方安全评估（CASA）** |

几点必须明确：

* 看到 **“此应用已被阻止 / This app is blocked”** 或 `access_denied`、`admin_policy_enforced`，
  原因在 Google 侧的授权策略（未登记为测试用户、应用未验证、Workspace 管理员禁止了第三方应用等），
  **无法通过修改代码绕过**，只能按上表调整 Google Cloud 项目配置或联系 Workspace 管理员。
* 本项目**不内置、也不会使用**其他开源项目或其他应用的 OAuth Client ID，更不会冒充其他应用
  （例如 Thunderbird）。每位用户/分发者都应使用自己创建的 Client ID。
* Refresh Token 失效（测试模式 7 天到期、用户撤销授权、修改密码等）时，应用会提示“授权已失效，请删除后重新登录”。

## 登录浏览器与代理

登录时打开的是**系统默认浏览器**。浏览器是独立进程，访问 `accounts.google.com` 时使用的是
**浏览器自己的 / 系统的代理设置**，Keepraw Mail 的代理配置**无法控制外部浏览器**。
如果你的网络需要代理才能访问 Google，请确保浏览器本身可以打开 Google 登录页。

授权完成后浏览器会跳转到 `http://127.0.0.1:<随机端口>/`，这是本机回环地址，不经过任何代理。
之后的 **Token 交换、Token 刷新、用户信息请求、IMAP、SMTP** 全部走账号的生效代理（见 [代理配置](ProxyConfiguration.md)）。

> **安全提示**：Client ID / Client Secret 一旦出现在提交记录中就应视为已泄露。
> 如果你之前把它们提交到了公开仓库，请立刻在
> [Google Cloud Console](https://console.cloud.google.com/apis/credentials) 中
> **删除并重新创建**这组凭据。
