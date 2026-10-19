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

## 步骤 5：在 Keepraw Mail 中使用

1.  打开 Keepraw Mail 设置（未来版本支持）。
2.  将您的 Client ID 填入相应位置。
3.  现在您可以使用 "Sign in with Google" 登录了！
