# NT8 Guard

![NT8 Guard - Quick Start](docs/guide/NT8%20Guard%20-%20Quick%20Start.png)

**Keeps NinjaTrader 8 running on its own.** It starts NinjaTrader again and logs in when it closes or
crashes, restarts it when it freezes, brings a dead market-data feed back, puts back strategies
NinjaTrader dropped (onto their charts) - and tells your phone what it did. Windows 10/11, your own user
account, no administrator rights. **Beta (0.9.0).**

- **Install:** download `NT8Guard-v0.9.0.zip` from the Releases page, *Extract All*, double-click
  `Install.cmd`. The setup wizard does the rest.
- **The guide:** [NT8 Guard - Setup Guide.pdf](docs/guide/NT8%20Guard%20-%20Setup%20Guide.pdf) - every step
  with screenshots, what happens when NinjaTrader crashes or the feed dies, troubleshooting by symptom.

## What is in this folder

| Folder | What |
|---|---|
| `app\` | The PowerShell program: the watchdogs, the login helper, the control panel and its setup wizard, the installer. Its [README](app/README.md) is the full reference: every setting, every rule. |
| `python\` | The strategy-backup tools (standard library only). The release bundles them with an embeddable Python. |
| `build\` | `Build-Release.ps1` - every gate, then the release zip; the add-ons zip, the guide's screenshots and its PDF. |
| `docs\guide\` | The setup guide and the Quick Start (HTML sources, the rendered PDF and PNG, the screenshots). |
| `Install.cmd` | The installer at the release zip's root. |

The two NinjaTrader add-ons it needs - the **feed guard** (`addon/AutoReconnect.cs`) and the **NT8 link**
(`addon/NT8BridgeServer.cs` + `NT8BridgeServerPlayback.cs`) - live in this repository's `addon/` folder and
ship in the release as `NT8Guard-AddOns.zip`, for NinjaTrader's *Tools > Import > NinjaScript Add-On*.

## Building a release

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File guard\build\Build-Release.ps1
```

It refuses to produce a zip unless every test suite passes, the add-ons compile, the bundled Python
matches its pinned SHA-256 and is signed by the Python Software Foundation, a scan finds no personal
strings, and the finished zip installs into a clean temporary profile.

## Licence

MIT ([LICENSE](LICENSE)). Third-party parts and their licences: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
NT8 Guard is not made, endorsed or supported by NinjaTrader, LLC.
