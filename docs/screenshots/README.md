# How to add screenshots

The splash page (`01-splash.png`) is captured automatically since it's anonymous.

For the **authenticated views** you need to capture them yourself (only authorized users can sign in):

1. Open https://intune-assignment-viewer.azurewebsites.net/ and sign in.
2. Take screenshots of these views and save them in this folder using the suggested names:

| File name | What to capture |
|---|---|
| `02-home.png` | The authenticated Home (hero + feature cards) |
| `03-search.png` | The Assignments page mid-typing showing the live group results dropdown |
| `04-dashboard.png` | A selected group showing the summary dashboard (total + bar chart + donut) |
| `05-cards.png` | The card grid view of assignments |
| `06-table.png` | The table view of assignments |
| `07-filters.png` | The page with an OS filter applied (e.g. `🪟 Windows`) |

3. Recommended browser window size: **1440 × 900**.
4. Tools: Win + Shift + S (Snipping Tool) on Windows, or the browser's screenshot extension.
5. Commit and push — the README will reference them automatically.

## Re-capturing the splash page

If you change the splash, run the helper script in `<repo>/scripts/capture-splash.ps1`.
