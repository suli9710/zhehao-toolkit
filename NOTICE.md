# 项目与组件来源

喆好工具箱基于 [Lypc233/LaoYing-Toolkit v1.8.4](https://github.com/Lypc233/LaoYing-Toolkit/releases/tag/v1.8.4) 发布包定制，上游产品名称为 LaoYing Toolkit / 老嘤工具箱，作者为 Lypc233（老嘤评测）。

本仓库包含品牌定制、WPF 关于页、自有 GitHub 更新运行库、构建及验证脚本。上游公开仓库和版本源代码归档仅包含说明与图片，因此本仓库不包含上游未公开的完整程序源码。构建时从上游下载并校验官方 v1.8.4 EXE。

上游组件的版权及许可仍属于各自权利人。保留原始程序集标识、第三方组件与资源、配置兼容性及第三方授权流程。第三方付费功能的授权要求不因品牌定制而改变。本定制版本由本仓库维护者发布。

修改后的发布包不保留原作者的 Windows Authenticode 签名；喆好工具箱在线更新使用本项目的 RSA-PSS 签名清单，签名公钥内置于 EXE。

构建脚本使用 [Mono.Cecil](https://github.com/jbevain/cecil) 0.11.6（MIT）、[pefile](https://github.com/erocarrera/pefile)（MIT）、[Pillow](https://github.com/python-pillow/Pillow)（HPND）和 [PySide6](https://doc.qt.io/qtforpython-6/licenses.html)（LGPLv3/GPLv3 或商业许可）。它们的许可适用于各自代码；新增源码不对上游及第三方组件的许可作重新授予。
