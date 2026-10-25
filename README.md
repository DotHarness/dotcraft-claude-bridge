<div align="center">

![DotCraft Claude Bridge](https://github.com/DotHarness/resources/raw/master/dotcraft-claude-code/banner.png)

[DotCraft](https://github.com/DotHarness/dotcraft) · [License](./LICENSE)

Use your Claude subscription for DotCraft.

</div>


## Build

Requires the .NET 10 SDK, Node.js 20 or later, and PowerShell 7.

```powershell
.\build.bat
```

## Install

1. Install Claude Code.
2. In DotCraft's **Plugins** page, choose **Install from disk** and select
   `build/claude-code`. Enable the plugin and trust its .NET extension.
3. In model settings, add a **Claude Code** provider, sign in, and select a model.
