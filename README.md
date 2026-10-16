# PayNow sales

A plugin for [Brick-Bread/WNotch](https://github.com/Brick-Bread/WNotch) that celebrates every sale on your [PayNow](https://paynow.gg) store. When someone buys something, the pill shows who bought what, with their avatar and the price, and the glow around it plays an animation. Needs **WNotch 0.12 or newer** (plugin API 7).

## What it does

- **An alert for each sale.** "Steve bought 1,000 Gems · €2.99", with the buyer's Minecraft or Steam avatar. More than three sales at once arrive as one alert ("5 new sales · €23.40").
- **An animation on the glow.** Pick one in Settings:

  | Animation | Looks like |
  |---|---|
  | **Confetti** | Coloured sparks popping all along the pill. |
  | **Gold** | A bright gold shimmer sweeping across a gold glow. |
  | **Rainbow** | A rainbow racing along the pill and slowing as it fades. |
  | **Fireworks** | Bursts blooming out from points along the pill. |
  | **Cash** | "Cha-ching": two quick green flashes and a green glow. |
  | **Off** | Just a green flash. |

- **Big sales** (an amount you choose, €20 by default) get a longer, wider animation and a star.
- **A reminder until you look.** After the alert the pill keeps showing "3 new sales · €8.97" until you open the PayNow tab or the Plugins tab. Can be switched off.
- **A card and a tab.** The card shows today's takings and the last buyer; the **PayNow** tab shows today, this month and all time, and a table of the latest sales. **Test alert** on the tab plays the alert and animation for a pretend sale.

## Setup

1. In WNotch Settings, under **Plugins**, enter `Brick-Bread/wnotch-paynow` and press **Install**.
2. In the PayNow dashboard, create an API key (Settings > API Keys) that can read orders. Copy your store ID from Settings > General.
3. Under the plugin in WNotch Settings, fill in **Store ID** and **API key**, then save.

The first time it sees your store the plugin only takes note of the orders already there; it announces sales from then on. Sales made while Notch was closed are announced when it starts again, if they are less than six hours old.

## Options

Store ID, API key, the animation, the big-sale amount, whether to keep a reminder in the pill, whether to skip free (fully discounted) orders, and how often to check (every 20 seconds by default; 10 to 600).

## Privacy and security

The plugin declares the `network` permission. It talks to `api.paynow.gg` with your key, reading your store's completed orders and its sales figures, and nothing else; it never changes anything in your store. Buyer avatars are downloaded from the address PayNow gives for them (such as crafthead.net), **without** your key.

The API key you type into Settings is encrypted for your Windows account (DPAPI) on the plugin's first start and the plain copy is removed from `settings.json`. Give the key the least access PayNow allows: reading orders (and statistics, for the figures) is all it needs.

PayNow can send webhooks, but only to a public web address, which a desktop PC usually does not have. That is why the plugin checks for new orders on a timer instead.

## Build

Requires the .NET 10 SDK and a WNotch build with API 7 or newer. The default reference is the installed `%LocalAppData%\Programs\Notch\Notch.Core.dll`.

```powershell
.\build.ps1
```

For another installation or a source build: `.\build.ps1 -NotchCorePath C:\path\to\Notch.Core.dll`. To try it without installing, quit Notch and run `Notch.exe --plugin=dist\brick-bread.paynow`.

- `PayNowClient.cs` reads orders and figures from the PayNow Management API.
- `Celebrations.cs` has the glow animations, one small class each.
- `PayNowPlugin.cs` polls, decides what is new, and runs the alerts, card and tab.

## Releasing

Raise `version` in `plugin.json`, push a tag `v<version>`; the workflow builds the plugin and attaches the single zip to a release.

MIT licensed.
