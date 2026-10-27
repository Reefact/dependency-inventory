// Renders a chart page built by build_chart.py to a PNG at twice its size, in the light theme,
// without the collapsible data table (it cannot be opened in an image).
// Usage: node render_png.js <chart.html> <chart.png>
const path = require("path");
const { chromium } = require("playwright");

(async () => {
  const [input, output] = process.argv.slice(2);
  if (!input || !output) {
    console.error("Usage: node render_png.js <chart.html> <chart.png>");
    process.exit(2);
  }
  const browser = await chromium.launch();
  try {
    const page = await browser.newPage({ viewport: { width: 1180, height: 400 }, deviceScaleFactor: 2, colorScheme: "light" });
    const errors = [];
    page.on("pageerror", e => errors.push(e.message));
    await page.goto("file://" + path.resolve(input), { waitUntil: "networkidle" });
    await page.evaluate(async () => {
      document.querySelector("details")?.remove();
      await document.fonts.ready;
    });
    if (errors.length) throw new Error("The page failed: " + errors.join("; "));
    const fontLoaded = await page.evaluate(() => document.fonts.check("600 12px Inter"));
    await page.screenshot({ path: output, fullPage: true });
    console.log(`${output}${fontLoaded ? "" : " (Inter not loaded: rendered with the fallback font)"}`);
  } finally {
    await browser.close();
  }
})().catch(error => {
  console.error(error.message);
  process.exit(1);
});
