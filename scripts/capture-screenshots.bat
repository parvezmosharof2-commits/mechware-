@echo off
if not exist "f:\CHAT GPT\WEBSIDE\docs\screenshots" mkdir "f:\CHAT GPT\WEBSIDE\docs\screenshots"
set CHROME="C:\Program Files\Google\Chrome\Application\chrome.exe"
set OUTDIR=f:\CHAT GPT\WEBSIDE\docs\screenshots

echo Capturing 1440px Dark...
%CHROME% --headless --disable-gpu --virtual-time-budget=5000 --window-size=1440,1200 --screenshot="%OUTDIR%\design-system-desktop-dark.png" "http://localhost:3008/en/design-system?theme=dark"

echo Capturing 1440px Light...
%CHROME% --headless --disable-gpu --virtual-time-budget=5000 --window-size=1440,1200 --screenshot="%OUTDIR%\design-system-desktop-light.png" "http://localhost:3008/en/design-system?theme=light"

echo Capturing 360px Dark...
%CHROME% --headless --disable-gpu --virtual-time-budget=5000 --window-size=360,800 --screenshot="%OUTDIR%\design-system-mobile-dark.png" "http://localhost:3008/en/design-system?theme=dark"

echo Capturing 360px Light...
%CHROME% --headless --disable-gpu --virtual-time-budget=5000 --window-size=360,800 --screenshot="%OUTDIR%\design-system-mobile-light.png" "http://localhost:3008/en/design-system?theme=light"

echo All screenshots captured!
dir "%OUTDIR%"
