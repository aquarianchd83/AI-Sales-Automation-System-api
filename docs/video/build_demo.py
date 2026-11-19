# -*- coding: utf-8 -*-
"""
Builds the two narrated demo videos (business and developer voiceovers) over one recording of the real Billing screen.

  py -3 docs/video/build_demo.py <angularRepoDir>

Needs the mock backend on :7099 (node docs/video/demo/mock-server.js) and the Angular dev server on :4300 started with a proxy to it
(see docs/video/README.md). Uses build.py for narration, slide cards and ffmpeg helpers.
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile

import build as B
import content_demo as C

HERE = os.path.dirname(os.path.abspath(__file__))


def storyboard(v, rows):
    out = [f"# {v['title']} - script and storyboard", "",
           f"Video: `docs/video/out/{v['slug']}/{v['slug']}.mp4`. Voice: {v['voice']}. The picture is a recording of the real Angular Billing screen against a "
           f"stand-in backend (`docs/video/demo/`); each demo video is its own recording, paced to its voiceover.", ""]
    for i, (name, d, a, b, text) in enumerate(rows, 1):
        out += [f"## Scene {i}: {name}", "", f"- **Time:** {int(a) // 60}:{int(a) % 60:02d} - {int(b) // 60}:{int(b) % 60:02d} ({d:.0f} s)", "", f"> {text}", ""]
    return '\n'.join(out)


def main(angular_dir):
    tmp = tempfile.mkdtemp(prefix='autopay_demo_')
    try:
        # 1. Narration first: how long each scene must last is decided by its longer voiceover.
        wavs, need = {}, {}
        for key, v in C.VARIANTS.items():
            texts = [('intro', v['intro']), ('outro', v['outro'])] + [(s, v['scenes'][s]) for s in C.SCENES]
            for name, text in texts:
                w = os.path.join(tmp, f'{key}_{name}.wav')
                B.speak(text, v['voice'], w)
                wavs[(key, name)] = (w, B.duration(w))
        def record(key):
            """Records the screen once for one voiceover, with each scene held just long enough for that voiceover - so the two videos are
            paced to their own narration rather than both to the longer one."""
            holds = {s: wavs[(key, s)][1] + B.PAD + 0.4 for s in C.SCENES}
            print(f'  [{key}] scene lengths:', {k: round(x, 1) for k, x in holds.items()})
            rec = os.path.join(tmp, 'rec_' + key)
            r = subprocess.run(['node', os.path.join(HERE, 'demo', 'record.js'), angular_dir, rec, json.dumps(holds)], capture_output=True, text=True)
            if r.returncode != 0:
                raise RuntimeError('recording failed: ' + r.stdout[-800:] + r.stderr[-1500:])
            marks = json.load(open(os.path.join(rec, 'marks.json')))
            meta = json.load(open(os.path.join(rec, 'meta.json')))
            # Frames are saved only when the picture changes, so the last one has to be held to the end of the final scene.
            raw = os.path.join(tmp, 'raw_' + key + '.mp4')
            B.run(['ffmpeg', '-y', '-loglevel', 'error', '-f', 'concat', '-safe', '0', '-i', os.path.join(rec, 'frames.ffconcat'),
                   '-vf', 'fps=30,scale=1920:1080:flags=lanczos,tpad=stop_mode=clone:stop_duration=40,format=yuv420p', '-c:v', 'libx264', '-preset', 'medium', '-crf', '20', raw])
            # Marks count from when recording began, frame times from the first frame.
            t0 = meta['t0Epoch'] - meta['firstFrameEpoch']
            return raw, [max(0.0, t0 + marks[s]) for s in C.SCENES + ['end']]

        for key, v in C.VARIANTS.items():
            raw, starts = record(key)
            out = os.path.join(B.OUT, v['slug'])
            os.makedirs(out, exist_ok=True)
            clips, cues, rows, t = [], [], [], 0.0
            enc = ['-c:v', 'libx264', '-preset', 'medium', '-crf', '20', '-c:a', 'aac', '-b:a', '160k', '-ar', '44100', '-ac', '2', '-r', '30', '-pix_fmt', 'yuv420p']

            def card(name, spec, text):
                nonlocal t
                base = os.path.join(tmp, f'{key}_{name}')
                with open(base + '.html', 'w', encoding='utf-8') as f:
                    f.write(B.render_slide(dict(kind='title', **spec), 1, 1, v['title']).replace('1 / 1', ''))
                B.shot(base + '.html', base + '.png')
                w, nd = wavs[(key, name)]
                d = nd + B.PAD
                B.run(['ffmpeg', '-y', '-loglevel', 'error', '-loop', '1', '-framerate', '30', '-i', base + '.png', '-i', w, '-af', f'apad=pad_dur={B.PAD}',
                       '-t', f'{d:.3f}', '-vf', f'fade=t=in:st=0:d=0.4,fade=t=out:st={d - 0.4:.3f}:d=0.4,format=yuv420p'] + enc + [base + '.mp4'])
                clips.append(base + '.mp4')
                cue(text, nd, t)
                rows.append((name, d, t, t + d, text))
                t += d

            def cue(text, narr, at):
                sents = B.sentences(text)
                chars = sum(len(x) for x in sents)
                c = at + 0.2
                for x in sents:
                    span = (narr - 0.2) * len(x) / chars
                    cues.append((c, c + span, x))
                    c += span

            card('intro', C.INTRO, v['intro'])
            for i, s in enumerate(C.SCENES):
                w, nd = wavs[(key, s)]
                seg = starts[i + 1] - starts[i]
                base = os.path.join(tmp, f'{key}_{s}')
                B.run(['ffmpeg', '-y', '-loglevel', 'error', '-ss', f'{starts[i]:.3f}', '-t', f'{seg:.3f}', '-i', raw, '-i', w,
                       '-af', f'apad=whole_dur={seg:.3f}', '-vf', 'fade=t=in:st=0:d=0.25,format=yuv420p', '-t', f'{seg:.3f}'] + enc + [base + '.mp4'])
                clips.append(base + '.mp4')
                cue(v['scenes'][s], nd, t)
                rows.append((s, seg, t, t + seg, v['scenes'][s]))
                t += seg
                print(f'  [{v["slug"]}] {s}: {seg:.1f}s (narration {nd:.1f}s)')
            card('outro', C.OUTRO, v['outro'])

            lst = os.path.join(tmp, f'{key}_list.txt')
            open(lst, 'w', encoding='utf-8').write(''.join(f"file '{c.replace(chr(92), '/')}'\n" for c in clips))
            srt = os.path.join(out, v['slug'] + '.srt')
            with open(srt, 'w', encoding='utf-8') as f:
                for k, (a, b, x) in enumerate(cues, 1):
                    f.write(f"{k}\n{B.srt_time(a)} --> {B.srt_time(b)}\n{x}\n\n")
            joined = os.path.join(tmp, key + '_joined.mp4')
            B.run(['ffmpeg', '-y', '-loglevel', 'error', '-f', 'concat', '-safe', '0', '-i', lst, '-c', 'copy', joined])
            final = os.path.join(out, v['slug'] + '.mp4')
            B.run(['ffmpeg', '-y', '-loglevel', 'error', '-i', joined, '-i', srt, '-c', 'copy', '-c:s', 'mov_text', '-metadata:s:s:0', 'language=eng', '-movflags', '+faststart', final])
            open(os.path.join(HERE, v['slug'] + '-storyboard.md'), 'w', encoding='utf-8').write(storyboard(v, rows))
            # A few stills so the result can be checked without playing it.
            for j, (name, d, a, b, _) in enumerate(rows):
                if name in ('confirmed', 'failure', 'cancel'):
                    B.run(['ffmpeg', '-y', '-loglevel', 'error', '-ss', f'{b - 1.2:.2f}', '-i', final, '-frames:v', '1', os.path.join(out, f'still-{name}.png')])
            print(f'  [{v["slug"]}] {final}  {B.duration(final):.0f}s  {os.path.getsize(final) / 1e6:.1f} MB')
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == '__main__':
    main(sys.argv[1])
