# AutoPay videos

Six deliverables, built from the sources in this folder. Outputs are in `out/` (not meant to be committed: they are large and rebuildable);
the scripts and storyboards are the `*-storyboard.md` files here.

| Audience | Narrated explainer (slides) | Recorded demo (real Billing screen) |
|---|---|---|
| Business, ~3 min | `out/overview/overview.mp4` | `out/demo-overview/demo-overview.mp4` |
| Developers, ~8 min | `out/technical/technical.mp4` | `out/demo-technical/demo-technical.mp4` |

Each video also has an `.srt` caption file (the MP4 carries the same captions as a switchable track) and each has a storyboard:
`overview-storyboard.md`, `technical-storyboard.md`, `demo-overview-storyboard.md`, `demo-technical-storyboard.md`. A storyboard is the shot
list with the narration text, so a person can re-record the voiceover.

## What the demo is

A recording of the **real Angular Billing screen** running in headless Chrome against `demo/mock-server.js`, a small stand-in for the API and
for Razorpay. The stand-in is moved between states on cue (pending, confirmed, failed, ...). A banner is burned into every frame saying so.
It shows how the screen behaves in each state. It does **not** show Razorpay, a UPI app, a webhook arriving, or a real payment.

The explainer slides state the same limits.

## Rebuilding

Needs: Python 3, Node, ffmpeg and ffprobe on PATH, Chrome (or Edge), Windows PowerShell with the built-in `System.Speech` voices
(Zira and David), and the Angular repo (`../AI-Sales-Automation-System-angular`) with its `node_modules`.

```bash
# slide videos
py -3 docs/video/build.py            # both
py -3 docs/video/build.py overview   # or one

# demo videos: start the stand-in backend, and the Angular app pointed at it
node docs/video/demo/mock-server.js 7099 &
cd ../AI-Sales-Automation-System-angular
npx ng serve --port 4300 --proxy-config ../AI-Sales-Automation-System-api/docs/video/demo/mock-proxy.json &
# when it has compiled:
cd ../AI-Sales-Automation-System-api
py -3 -c "import sys; sys.path.insert(0,'docs/video'); import build_demo; build_demo.main(r'../AI-Sales-Automation-System-angular')"
```

Change what is said in `content_overview.py`, `content_technical.py` and `content_demo.py`; scene lengths follow the narration.
