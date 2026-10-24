# ClickToPlay

单击出牌、快捷出牌、目标锁定、自动确认选牌及可配置的回合结束辅助。

这是已发布版本 **0.6.6** 的源代码快照。[Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3748287741)。原作者署名保留在 mod 清单中。

## 构建

需要 .NET SDK（net10.0）、Godot 4.5.1 .NET SDK，以及你自己安装的《杀戮尖塔 2》。ClickToPlay 与音效工坊需要 BaseLib。

```powershell
dotnet build ClickToPlay.csproj -c Release -p:Sts2Dir="<游戏目录>" -p:BaseLibDir="<BaseLib目录>"
```

也可设置环境变量 `STS2_DIR`、`BASELIB_DIR`。`Sts2DataDir` 可以单独指定，以适配不同的游戏目录布局。

项目保留运行时程序集名称 `ClickToPlay`，请勿随仓库名称修改。构建不会自动安装或覆盖游戏里的 mod。输出位于 `.godot/mono/temp/bin/Release/`。发行时使用同名 DLL、JSON 和 PCK。

## 范围与兼容性

这里只发布代码和设置界面本地化，不包含游戏程序集、用户配置、游戏存档、私有音频或游戏素材。ClickToPlay 本地化资源位于 `ClickToPlay/localization/`，用 Godot 导出资源包并保持这个资源路径。

游戏更新可能改变内部 API。具体构建与验证记录见 [VALIDATION.md](VALIDATION.md)；编译通过不等于所有游戏内交互均已重新测试。

原始代码采用 [MIT](LICENSE) 许可；第三方依赖见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
