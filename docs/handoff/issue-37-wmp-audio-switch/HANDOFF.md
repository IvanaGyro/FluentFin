# Handoff: audio menu while transcoding (issue 37) and Windows Media Player playback

Written 2026-11-03 for the next agent. The user (Ivana) writes to agents in Traditional Chinese; reply in Traditional Chinese.

- Upstream issue: <https://github.com/insomniachi/FluentFin/issues/37> ("The audio menu of the player is empty while transcoding"). The GitHub MCP tools of the session only reach `ivanagyro/fluentfin`; read the upstream issue through the web.
- Fork PR: <https://github.com/IvanaGyro/FluentFin/pull/11>, branch `feature/issue-37`, base `ivana-main` (not `master`; the user asked for `ivana-main`).
- Last code commit when this was written: `871bd26`. CI (`Tests`) green on it. Codex review of it: no findings.
- Last build: run `41507103971`, artifacts `13171292313` (x64) and `13172326024` (arm64). The artifact list shows `head_sha` of the workflow's own branch (`fork-ci`), not of the branch that was built; the built branch is the `ref` input of the run.

## The one open problem

**On Windows Media Player, the last switch to a transcoded audio track still shows the first frame of the video (the start of the file) once, before the picture goes to the position the item was at.** The time bar no longer goes to zero, the sound is right, the position is right; only a frame of the start is visible for a moment when the black cover is lifted.

The log of that run is `logs/2026-11-03-wmp-last-transcode-switch.log` (arm64 build, 4K HEVC, remote Jellyfin, `FluentFin.WindowsMediaPlayer` logger). What it shows, by line:

| Lines | What happened |
| --- | --- |
| 13–61 | First play, direct play, resumes at 44:23. `Video shown` at `Paused` right when `Seek completed`, 0.7 s after the media opened. No flash reported. |
| 62–111 | Switch #2 to a transcode (DTS is not decodable on this machine, `TranscodeReasons=AudioCodecNotSupported`). Hidden at 52.465. Playlist and tracks at 59.6 (7 s), `Buffering` until 22:09.801, `Video shown` at 22:10.212 together with `Seek completed`, 0.4 s after `Playing`/`Buffering ended`. No flash reported. |
| 112–164 | Switch #3 back to direct play. No flash reported. |
| 165–194 | Ten in-player switches (direct play, `AudioSwitchPolicy`). Fine. |
| **195–243** | **Switch #4 to a transcode: the flash.** `Buffering started/ended/started` and `Seek completed` all at 24:07.866, i.e. `SeekCompleted` fires at once while the player still buffers. Then nothing for 15 s. At 24:23.174 `Playing`, then `Buffering ended`, and `Video shown` is logged in the same millisecond (the gate opened on `BufferingEnded`). |

Difference between #2 (no flash) and #4 (flash): in #2 the gate was opened by a `SeekCompleted` that arrived 0.4 s after `Playing`+`Buffering ended`; in #4 `SeekCompleted` came 15 s earlier, so the gate opened in the very millisecond the buffering ended, and the element presented a frame of the start of the stream.

### Hypotheses (none is verified)

1. The `MediaPlayerElement` still holds the first frame it rendered when the media opened (position 0, before the seek) and shows it until the first frame after the seek is presented. Lifting `Opacity` to 1 at `BufferingEnded` shows that stale frame. In #2 the extra 0.4 s was enough for the new frame.
2. `SeekCompleted` is not a reliable "the frame at the target is on screen" signal for an HLS transcode: it fired at the start of the buffering in #4.

### Ideas, in the order I would try them

- Keep the video hidden until the position has advanced a little past the target while `Playing` and not buffering (for example 300–500 ms of playback after the target), instead of opening at `BufferingEnded`/`SeekCompleted`. The condition lives in `WmpSeeker.EvaluateVideo`.
- Use a new `MediaPlayer` (and `SetMediaPlayer` on the element) for every media instead of reusing the one in `WindowsMediaPlayerController`, so no frame of the previous load can be presented.
- Measure what the element presents. The existing test (`WmpSwitchTests`) can only see the frames the player produces (`IsVideoFrameServerEnabled` + `VideoFrameAvailable`, each with the position at that moment and whether the gate would have shown it); it cannot see the element. A test that reproduces the flash needs either a hosted element (`RenderTargetBitmap` of a `MediaPlayerElement` in a window on the runner) or a trace that follows the frame that is presented. Do not claim a fix without a reproduction; the user asked for that more than once ("if you do not reproduce it, how do you write the test").
- To reproduce the ordering of #4 in `WmpSwitchTests`: make the fake server delay the first segment (`FakeMediaServer.SegmentDelay`) so that `SeekCompleted` comes while the player is buffering, then check the frames shown after `Playing`.

## What this branch does

### Feature (the issue)
- While transcoding, the audio menu lists the audio streams of the **source file** (`MediaSourceInfo.MediaStreams`). Picking one asks the server again with that `AudioStreamIndex` and resumes at the current position; the server decides direct play or transcode. The same menu is used for every play method and every player, so the names come from one place (the language is a name such as "Chinese", not the ISO code "zho").
- Like the web client: for a **direct play**, if the device profile plays the codec of the target stream, the player switches the track itself (no request, no reload). `FluentFin.Core/Services/AudioSwitchPolicy.cs` decides: container and audio codec in a `DirectPlayProfiles` entry of type Video, and the `VideoAudio` codec profile conditions (channels, sample rate, bitrate, audio profile; a required condition on a value the stream does not have, or an unknown kind, goes to the server). The user chose **not** to add a fallback "if the player switch is silent, ask the server": silence would mean the profile is wrong, which is to be fixed in the profile.
- The user's Windows media player has the Dolby extension: AC-3 and E-AC-3 are direct played; DTS is not (transcoded). `WmpDeviceProfileManager` builds the profile from the decoders of the machine.

### Where things are
| File | Role |
| --- | --- |
| `FluentFin.Core/Contracts/Services/IMediaPlayerController.cs` | `SourceAudioMenu` (the audio menu of the source file) and `HoldPosition(TimeSpan?)` (show a position until the player is at it). |
| `FluentFin.Core/Services/AudioSwitchPolicy.cs` | In-player switch or ask the server. |
| `FluentFin.Core/Services/PendingSeek.cs` | A seek asked for before the player can do it: target, tolerance 2 s, timeout 60 s, `ShouldReport`, `NeedsSeek`, `Reminders` (re-emits the held position every 250 ms, only while one is held, so a time bar that clamped the value to a short length is corrected). |
| `FluentFin/ViewModels/VideoPlayerViewModel.cs` | `SwitchAudioStream` (request numbers, `_audioSwitch` state with the resume position and paused state kept for retries, `_loadFailed`, rollback of only the audio part of the selection), `TrySwitchInPlayer`, `LoadSelectedItem` (returns whether it ended in playback; checks `_loadVersion` before `HoldPosition`), `BuildSourceAudioMenu`, `LanguageName`. |
| `FluentFin/MediaPlayers/WmpSeeker.cs` | Windows Media Player position and seek logic: defers the seek until the media is open and **pauses** the player meanwhile (`PlayRequested` keeps a pause of the app), `OnMediaOpened` (position first, then the audio track), the **video gate** (`IsVideoVisible`, `BeginLoading`, `EvaluateVideo`: hidden until opened, at the position, `Playing`/`Paused`, not buffering, and `SeekCompleted` after our last `SetPosition`; 75 s limit). |
| `FluentFin/MediaPlayers/WindowsMediaPlayerController.cs` | Uses the seeker; `Opacity` of the `MediaPlayerElement` follows the gate (`MediaPlayerHost.xaml` has a black `RootGrid`); an audio pick before the media is open replaces the startup track and is made when it is open; many log lines (`FluentFin.WindowsMediaPlayer`: opening, tracks with decoder status, selection, playback state with position, seek completed, buffering, video shown/hidden). |
| `FluentFin/MediaPlayers/VlcMediaPlayerController.cs`, `FlyleafMediaPlayerController.cs` | `HoldPosition`, `PendingSeek`; VLC applies a seek that was asked for too early when it can. **No video gate, not verified for the black screen or for the track-select timing problems below.** |
| `FluentFin/Controls/TransportControls.xaml.cs` | `_updatingSlider`: setting the slider (position, new length) is not a seek of the user; the position is given again when the length is known. |
| `FluentFin/Converters/Converters.cs` | `GetAudiosFlyout` uses `SourceAudioMenu` when set. |

### Platform findings about Windows Media Player (each has a test or a log)
1. An audio track selected while the tracks are announced (`AudioTracksChanged`, i.e. before the media is open) shows as selected with a supported decoder and **plays no sound**. Selecting it once the media is open (or later) does. (`WmpAudioPlaybackTests`, scenarios "silent expected".)
2. A track selected **before** the position is set restarts the playback from the start of the file; position first, then track, works (`WmpSeekTests`: `Baseline_ATrackSelectedBeforeThePosition_RestartsThePlayback`, the loop tests). This was the cause of "the item does not resume after leaving and coming back".
3. After the position is set before the media is open, the player reports that position and may report `Playing` and then `Buffering` a millisecond later while it still buffers; `SeekCompleted` can come early (the open problem).
4. A transcode of the 4K video: about 7 s for the playlist and tracks, 10–15 s of buffering.
5. On the CI runner Windows has no Dolby decoder (E-AC-3/AC-3/DTS are `UnsupportedSubtype`), AAC works; on the user's machine only DTS is missing.

## Tests and CI

There is **no .NET and no Windows in the cloud container**. Everything runs in GitHub Actions: pushing the branch runs `Tests` (`.github/workflows/tests.yml`: `Unit and style tests` on a Windows runner with a virtual audio device, VB-Cable, and `Flyleaf playback tests (arm64)`).

- Read results with `gh run view <run> -R IvanaGyro/FluentFin --json jobs`, then `gh run view --job <job id> -R IvanaGyro/FluentFin --log`. The step `Show what Windows Media Player plays` prints the numbers of `WmpAudioPlaybackTests`, `WmpSeekTests` and `WmpSwitchTests` even when they pass (`--logger "console;verbosity=detailed"`).
- A red `Install a virtual audio device` step is the runner failing to download VB-Cable (DNS); `gh run rerun <run> --failed -R IvanaGyro/FluentFin` once. `FlyleafAudioPlaybackTests` (aac) was flaky when audio tests ran in parallel; they now share the xunit collection `AudioDevice.Name`.
- `FluentFin.Core.Tests` (pure logic): `AudioSwitchPolicyTests`, `PendingSeekTests` (wait for conditions with `WaitFor`, not for times). `FluentFin.Tests` (needs the app project and Windows): `VideoPlayerAudioTrackTests` (menu, `IsAudioSwitch`, `SwitchFailed`, `RestoreAudio`, `LanguageName`), `WmpAudioPlaybackTests` (what reaches the audio device, measured with the peak meter of the default endpoint through NAudio), `WmpSeekTests` (seek, pause kept, resume loop, track order, in-player switch keeps the position, local HLS), `WmpSwitchTests` (the four switches direct↔transcode, and playing again, with a server that takes its time, a slow-server variant, and a profile-derived direct/transcode decision).
- Media in `FluentFin.Tests/Media`: `audio-tracks.mkv`, `hls/` (a 12 s HLS), `switch/tracks.mkv` (two AAC and two DTS tracks, 4.7 MB) with `switch/hls-d0` and `hls-d1` (the DTS tracks transcoded to AAC). Regenerate with `ffmpeg` (`-c:a dca -strict -2` for DTS; give no `-b:a` to the dca encoder).
- The tests drive `WmpSeeker` and a `MediaPlayer` directly with the same order of calls as the controller; the WinUI controller itself cannot be built in a test.

## Test build for the user

Workflow `test-build.yml` lives on the fork's default branch `fork-ci`. Run it with `mcp__github__actions_run_trigger` (`method: run_workflow`, `workflow_id: test-build.yml`, `ref: fork-ci`, `inputs: {"ref": "<branch>"}`), then poll with one long shell command (`until gh run list ... ; do sleep 10; done`), then call `list_workflow_run_artifacts` once and give the user `https://github.com/IvanaGyro/FluentFin/actions/runs/<run id>/artifacts/<artifact id>`. Do not read the job logs of that workflow. Always test-build after finishing a task.

## Working agreements with this user (from their instructions and the sessions)

- Base branch `ivana-main` for this work. Ivana is the co-author of every commit (`Co-authored-by: Ivana <ivana.gyro@gmail.com>`); no session links anywhere; no co-author in PR descriptions. The upstream repository is public.
- Every GitHub comment you write ends with the `_Generated by [Claude Code](https://claude.ai/code)_` footer. Codex (`chatgpt-codex-connector[bot]`) reviews each push; it found real edge cases in the asynchronous switch flow every round. Answer each thread, fix what is real and proportionate. Its usage limit ran out for a while (it posts "You have reached your Codex usage limits"); then `@codex review` after it is back.
- **Reproduce before fixing, and say plainly what is not verified.** The user caught me twice fixing from inference. State what a test measures and what it cannot see.
- They want: a black picture while the video loads, enough logs to tell what happened, tests with network delay and with the four kinds of switch (direct and transcode, two tracks each), the direct/transcode decision taken from the real profile.
- Project `CLAUDE.md` asks for `graphify query` before searching the code and `graphify update .` after changing code.

## Not done / not verified

- The open flash above.
- VLC and Flyleaf: no black cover, the order "position first, then track" and the "track selected too early is silent" problem were only tested for Windows Media Player.
- Real Jellyfin: all tests use a local server and local media; only the user's machine has the 4K remote transcode.
- A separate task was suggested (not started): the first sign-in fails with Windows Media Player because `WmpDeviceProfileManager.Profile` is read before `InitializeAsync` (line 4–10 of the log), and `StartAnimationAction` throws `ArgumentNullException (Parameter 'Animation')` on pointer events (lines of the older log, also present in the first run here). Neither is caused by this branch.
- `ivana-main` build used to check that "no sound" in Windows Media Player was a regression: run `41408219792`.

## What the log contains

The committed log has the server address of the user (a private address), the device id, the path of their Downloads folder and the item they played; the API key is masked (`ApiKey=***`). Nothing else secret was found in it.
