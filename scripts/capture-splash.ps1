# Re-captures the splash page screenshot using headless Chrome via Playwright.
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repoRoot "docs/screenshots"
$workDir = Join-Path $env:TEMP "intune-viewer-screenshot"

New-Item -ItemType Directory -Path $workDir -Force | Out-Null
Push-Location $workDir
try {
    if (-not (Test-Path "node_modules/playwright-core")) {
        Write-Host "Installing playwright-core..."
        npm init -y | Out-Null
        npm install playwright-core --no-fund --no-audit --silent
    }

    $chromePath = "C:\Program Files\Google\Chrome\Application\chrome.exe"
    if (-not (Test-Path $chromePath)) {
        $chromePath = "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
    }
    if (-not (Test-Path $chromePath)) {
        throw "Google Chrome not found."
    }

    $cp = $chromePath.Replace('\','\\')
    $od = $outDir.Replace('\','\\') + '\\01-splash.png'
    $script = "const { chromium } = require('playwright-core');`n(async () => {`n  const b = await chromium.launch({ executablePath: '$cp', headless: true });`n  const c = await b.newContext({ viewport: { width: 1440, height: 900 } });`n  const p = await c.newPage();`n  await p.goto('https://intune-assignment-viewer.azurewebsites.net/', { waitUntil: 'load', timeout: 60000 });`n  await p.waitForTimeout(5000);`n  await p.screenshot({ path: '$od' });`n  await b.close();`n  console.log('Saved');`n})();"
    Set-Content -Path "capture.js" -Value $script -Encoding UTF8
    node capture.js
}
finally {
    Pop-Location
}
