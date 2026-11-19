# -*- coding: utf-8 -*-
"""
Builds the AutoPay explainer videos from content_overview.py and content_technical.py. Standard library only.

  py -3 docs/video/build.py            # everything
  py -3 docs/video/build.py overview   # one video

Needs on PATH or in the usual place: ffmpeg + ffprobe, Google Chrome (for slide images), and Windows PowerShell with the built-in
System.Speech voices (for narration). Writes to docs/video/out/<slug>/ and the storyboards to docs/video/<slug>-storyboard.md.
Intermediate files go to a temp folder that is removed afterwards.
"""
import html
import importlib
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
OUT = os.path.join(HERE, 'out')
W, H = 1920, 1080
PAD = 0.9  # seconds of quiet after each slide's narration

CHROME = next((p for p in [
    os.path.expandvars(r'%ProgramFiles%\Google\Chrome\Application\chrome.exe'),
    os.path.expandvars(r'%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe'),
    os.path.expandvars(r'%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe'),
] if os.path.exists(p)), None)

CSS = """
*{box-sizing:border-box;margin:0;padding:0}
body{width:1920px;height:1080px;background:linear-gradient(135deg,#0b1f3a 0%,#10294b 60%,#0f3a4a 100%);color:#eef4fb;
 font-family:'Segoe UI',Arial,sans-serif;overflow:hidden;position:relative}
.frame{position:absolute;inset:0;padding:90px 120px 110px}
h1{font-size:84px;line-height:1.1;font-weight:700;letter-spacing:-1px}
h2{font-size:62px;line-height:1.15;font-weight:700;margin-bottom:56px;letter-spacing:-.5px}
.accent{color:#4fd1c5}
.sub{font-size:38px;color:#b7c9dd;margin-top:30px;max-width:1500px;line-height:1.35}
.tag{display:inline-block;margin-bottom:36px;padding:10px 26px;border:2px solid #4fd1c5;color:#4fd1c5;border-radius:40px;font-size:26px;letter-spacing:1px}
.title-slide{display:flex;flex-direction:column;justify-content:center;height:100%}
.bar{position:absolute;left:0;top:0;width:18px;height:100%;background:#4fd1c5}
.foot{position:absolute;left:120px;right:120px;bottom:34px;font-size:22px;color:#7f97b2;display:flex;justify-content:space-between}
.content{display:flex;flex-direction:column;justify-content:center;height:calc(100% - 150px);padding-bottom:20px}
.note{margin-top:46px;font-size:32px;color:#ffd479}
.steps{display:flex;gap:26px;align-items:stretch}
.step{flex:1;background:#13355c;border:2px solid #1f4b7d;border-radius:22px;padding:34px 26px;position:relative}
.step .n{width:64px;height:64px;border-radius:50%;background:#4fd1c5;color:#0b1f3a;font-weight:700;font-size:34px;display:flex;align-items:center;justify-content:center;margin-bottom:22px}
.step .l{font-size:34px;font-weight:700;margin-bottom:12px;line-height:1.2}
.step .s{font-size:25px;color:#b7c9dd;line-height:1.35}
.cards{display:grid;gap:30px}
.cards.c2{grid-template-columns:1fr 1fr}.cards.c3{grid-template-columns:1fr 1fr 1fr}.cards.c4{grid-template-columns:1fr 1fr}
.card{background:#13355c;border:2px solid #1f4b7d;border-radius:22px;padding:34px 38px}
.card .h{font-size:38px;font-weight:700;color:#4fd1c5;margin-bottom:14px;line-height:1.2}
.card .b{font-size:29px;color:#d5e2f0;line-height:1.38}
.items{display:flex;flex-direction:column;gap:22px}
.item{background:#13355c;border-left:10px solid #4fd1c5;border-radius:14px;padding:22px 34px}
.item .h{font-size:35px;font-weight:700;margin-bottom:6px}.item .b{font-size:28px;color:#b7c9dd;line-height:1.35}
table{width:100%;border-collapse:collapse;font-size:29px}
th{text-align:left;padding:16px 22px;background:#1f4b7d;color:#4fd1c5;font-size:27px;letter-spacing:.5px}
td{padding:16px 22px;border-bottom:1px solid #1f4b7d;color:#d5e2f0;line-height:1.3;vertical-align:top}
td:first-child{font-weight:700;color:#fff;white-space:nowrap}
.stats{display:flex;gap:40px}
.stat{flex:1;background:#13355c;border:2px solid #1f4b7d;border-radius:26px;padding:56px 30px;text-align:center}
.stat .v{font-size:130px;font-weight:700;color:#4fd1c5;line-height:1}.stat .t{font-size:34px;color:#b7c9dd;margin-top:22px}
.lane{display:flex;align-items:center;gap:12px;margin-bottom:34px;flex-wrap:nowrap}
.lane-label{width:230px;flex:0 0 230px;font-size:25px;color:#7f97b2;text-transform:uppercase;letter-spacing:1px;line-height:1.2}
.node{background:#13355c;border:2px solid #4fd1c5;border-radius:14px;padding:18px 20px;font-size:25px;font-weight:600;text-align:center;line-height:1.2}
.node.ext{background:#4fd1c5;color:#0b1f3a;border-color:#4fd1c5}.node.db{border-style:dashed;border-color:#ffd479;color:#ffd479}
.arrow{font-size:36px;color:#4fd1c5}
"""


def page(body, n, total, label):
    return (f"<!doctype html><html><head><meta charset='utf-8'><style>{CSS}</style></head><body><div class='bar'></div>"
            f"<div class='frame'>{body}</div><div class='foot'><span>{html.escape(label)}</span><span>{n} / {total}</span></div></body></html>")


def e(x):
    return html.escape(x)


def render_slide(s, n, total, label):
    k = s['kind']
    if k == 'title':
        body = (f"<div class='title-slide'><div><span class='tag'>{e(s['tag'])}</span></div><h1>{e(s['title'])}</h1>"
                f"<div class='sub'>{e(s['subtitle'])}</div></div>")
        return page(body, n, total, label)
    parts = [f"<h2>{e(s['title'])}</h2>"]
    if k == 'steps':
        parts.append("<div class='steps'>" + ''.join(
            f"<div class='step'><div class='n'>{e(a)}</div><div class='l'>{e(b)}</div><div class='s'>{e(c)}</div></div>" for a, b, c in s['steps']) + "</div>")
    elif k == 'cards':
        parts.append(f"<div class='cards c{len(s['cards']) if len(s['cards']) in (2, 3) else 4}'>" + ''.join(
            f"<div class='card'><div class='h'>{e(h)}</div><div class='b'>{e(b)}</div></div>" for h, b in s['cards']) + "</div>")
    elif k == 'bullets':
        parts.append("<div class='items'>" + ''.join(
            f"<div class='item'><div class='h'>{e(h)}</div><div class='b'>{e(b)}</div></div>" for h, b in s['items']) + "</div>")
    elif k == 'table':
        parts.append("<table><tr>" + ''.join(f"<th>{e(h)}</th>" for h in s['headers']) + "</tr>" + ''.join(
            "<tr>" + ''.join(f"<td>{e(c)}</td>" for c in row) + "</tr>" for row in s['rows']) + "</table>")
    elif k == 'stats':
        parts.append("<div class='stats'>" + ''.join(
            f"<div class='stat'><div class='v'>{e(v)}</div><div class='t'>{e(t)}</div></div>" for v, t in s['stats']) + "</div>")
    elif k == 'diagram':
        parts.append(s['html'])
    if s.get('note'):
        parts.append(f"<div class='note'>{e(s['note'])}</div>")
    return page(parts[0] + "<div class='content'>" + ''.join(parts[1:]) + '</div>', n, total, label)


def run(cmd, **kw):
    r = subprocess.run(cmd, capture_output=True, text=True, **kw)
    if r.returncode != 0:
        raise RuntimeError(f"{cmd[0]} failed ({r.returncode}):\n{r.stderr[-1500:]}")
    return r


def duration(path):
    return float(run(['ffprobe', '-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', path]).stdout.strip())


def speak(text, voice, wav):
    txt = wav + '.txt'
    with open(txt, 'w', encoding='utf-8') as f:
        f.write(text)
    ps = ("Add-Type -AssemblyName System.Speech;"
          "$s=New-Object System.Speech.Synthesis.SpeechSynthesizer;"
          f"$s.SelectVoice('{voice}');$s.Rate=-1;"
          f"$s.SetOutputToWaveFile('{wav}');"
          f"$s.Speak([IO.File]::ReadAllText('{txt}',[Text.Encoding]::UTF8));$s.Dispose()")
    run(['powershell', '-NoProfile', '-Command', ps])


def shot(htmlfile, png):
    url = 'file:///' + htmlfile.replace('\\', '/')
    run([CHROME, '--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
         f'--window-size={W},{H}', f'--screenshot={png}', '--virtual-time-budget=2000', url])
    if not os.path.exists(png):
        raise RuntimeError('no screenshot produced for ' + htmlfile)


def srt_time(t):
    ms = int(round(t * 1000))
    return f"{ms // 3600000:02d}:{ms // 60000 % 60:02d}:{ms // 1000 % 60:02d},{ms % 1000:03d}"


def sentences(text):
    return [p.strip() for p in re.split(r'(?<=[.!?])\s+', text) if p.strip()]


def storyboard(mod, durations):
    lines = [f"# {mod.TITLE} - script and storyboard", "",
             f"Video: `docs/video/out/{mod.SLUG}/{mod.SLUG}.mp4` - {len(mod.SLIDES)} scenes, about {int(sum(durations)) // 60} min {int(sum(durations)) % 60:02d} s. "
             f"Voice: {mod.VOICE}. Everything below can be re-recorded by a person by reading the narration.", ""]
    t = 0.0
    for i, (s, d) in enumerate(zip(mod.SLIDES, durations), 1):
        lines += [f"## Scene {i}: {s['title']}", "", f"- **Time:** {int(t) // 60}:{int(t) % 60:02d} - {int(t + d) // 60}:{int(t + d) % 60:02d} ({d:.0f} s)",
                  f"- **On screen:** {s['visual']}", "", f"> {s['narration']}", ""]
        t += d
    return '\n'.join(lines)


def build(slug):
    mod = importlib.import_module('content_' + slug)
    out = os.path.join(OUT, slug)
    os.makedirs(out, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix='autopay_video_')
    try:
        n = len(mod.SLIDES)
        clips, durations, cues, t = [], [], [], 0.0
        for i, s in enumerate(mod.SLIDES, 1):
            base = os.path.join(tmp, f's{i:02d}')
            with open(base + '.html', 'w', encoding='utf-8') as f:
                f.write(render_slide(s, i, n, mod.TITLE))
            shot(base + '.html', base + '.png')
            speak(s['narration'], mod.VOICE, base + '.wav')
            d = duration(base + '.wav') + PAD
            run(['ffmpeg', '-y', '-loglevel', 'error', '-loop', '1', '-framerate', '30', '-i', base + '.png', '-i', base + '.wav',
                 '-af', f'apad=pad_dur={PAD}', '-t', f'{d:.3f}', '-vf', f'fade=t=in:st=0:d=0.4,fade=t=out:st={d - 0.4:.3f}:d=0.4,format=yuv420p',
                 '-c:v', 'libx264', '-preset', 'medium', '-crf', '20', '-c:a', 'aac', '-b:a', '160k', '-ar', '44100', '-ac', '2', '-r', '30',
                 base + '.mp4'])
            clips.append(base + '.mp4')
            narr = duration(base + '.wav')
            sents = sentences(s['narration'])
            total_chars = sum(len(x) for x in sents)
            c = t + 0.2
            for x in sents:
                span = (narr - 0.2) * len(x) / total_chars
                cues.append((c, c + span, x))
                c += span
            durations.append(d)
            t += d
            print(f'  [{slug}] scene {i}/{n} {d:.1f}s')
            shutil.copy(base + '.png', os.path.join(out, f'scene-{i:02d}.png'))

        lst = os.path.join(tmp, 'list.txt')
        with open(lst, 'w', encoding='utf-8') as f:
            f.write(''.join(f"file '{c.replace(chr(92), '/')}'\n" for c in clips))
        srt = os.path.join(out, f'{slug}.srt')
        with open(srt, 'w', encoding='utf-8') as f:
            for k, (a, b, x) in enumerate(cues, 1):
                f.write(f"{k}\n{srt_time(a)} --> {srt_time(b)}\n{x}\n\n")
        joined = os.path.join(tmp, 'joined.mp4')
        run(['ffmpeg', '-y', '-loglevel', 'error', '-f', 'concat', '-safe', '0', '-i', lst, '-c', 'copy', joined])
        final = os.path.join(out, f'{slug}.mp4')
        # Captions are soft: a player can switch them on or off.
        run(['ffmpeg', '-y', '-loglevel', 'error', '-i', joined, '-i', srt, '-c', 'copy', '-c:s', 'mov_text', '-metadata:s:s:0', 'language=eng',
             '-movflags', '+faststart', final])
        with open(os.path.join(HERE, f'{slug}-storyboard.md'), 'w', encoding='utf-8') as f:
            f.write(storyboard(mod, durations))
        print(f'  [{slug}] {final}  {sum(durations):.0f}s  {os.path.getsize(final) / 1e6:.1f} MB')
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == '__main__':
    if not CHROME:
        sys.exit('Chrome or Edge not found.')
    for slug in (sys.argv[1:] or ['overview', 'technical']):
        build(slug)
