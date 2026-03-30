# Deep Dive Video Production Plan

## TODO
- [x] Scene 1: The 60% Tax (16s, 2 clips)
- [x] Scene 2: Autocomplete Is Not Autonomy (24s, 3 clips)
- [x] Scene 3: Fear of Autonomy (16s, 2 clips)
- [x] Scene 4: Tamma: It Is Done (16s, 2 clips)
- [x] Scene 5: End-to-End Autonomy (18s, 2 clips)
- [x] Scene 6: You Stay in Control (16s, 2 clips)
- [x] Scene 7: Any AI, Your Choice (20s, 2 clips)
- [x] Scene 8: Every Git Platform (16s, 2 clips)
- [x] Scene 9: Mandatory Quality Gates (18s, 2 clips)
- [x] Scene 10: Time-Travel Debugging (24s, 3 clips)
- [x] Scene 11: Visual Workflow Orchestration (20s, 2 clips)
- [x] Scene 12: Intelligent Agent Routing (24s, 3 clips)
- [x] Scene 13: Tamma Maintains Itself (24s, 3 clips)
- [x] Scene 14: Sarah's Story (30s, 3 clips)
- [x] Scene 15: AI That Learns (16s, 2 clips)
- [x] Scene 16: Built for Production (24s, 3 clips)
- [x] Scene 17: Where We Are (24s, 3 clips)
- [x] Scene 18: Join the Movement (20s, 2 clips)
- [x] Stitching & Transitions
- [x] Final Render

---

## Technical Pipeline Overview

### Video Generation: Runway Gen4 Turbo via Freepik API

All image-to-video clips are generated through Freepik's hosted Runway Gen4 Turbo model. Video only -- no audio is generated from the video model.

- **Endpoint**: `POST https://api.freepik.com/v1/ai/image-to-video/runway-4-5`
- **Auth Header**: `x-freepik-api-key: <API_KEY>`
- **Request Body**:
  - `image` (string, required): Base64-encoded image or publicly accessible HTTPS URL
  - `prompt` (string, optional, max 1000 chars): Motion/camera description for the clip
  - `duration` (integer, optional): `5`, `8`, or `10` seconds (default: `10`)
  - `ratio` (string, optional): Aspect ratio -- `1280:720`, `720:1280`, `1104:832`, `832:1104`, `960:960`, `1584:672` (default: `1280:720`)
  - `seed` (integer, optional): `0`-`4294967295` for reproducibility
  - `webhook_url` (string, optional): Callback URL on completion
- **Response** (201):
  ```json
  { "data": { "task_id": "uuid", "status": "CREATED" } }
  ```
- **Polling**: `GET https://api.freepik.com/v1/ai/image-to-video/runway-4-5/<task_id>`
  - Poll until `status` is `COMPLETED` or `FAILED`
  - On completion, `data.generated` contains an array of video URLs
- **Pricing**: ~$0.12/second of generated video
- **Resolution**: All clips rendered at 1280x720 (`ratio: "1280:720"`) then upscaled to 1080p in post-production

#### Per-Clip Generation Pattern

```bash
# Example: Scene 1, Clip 1 (8 seconds, Frame A -> Frame B)
curl -X POST "https://api.freepik.com/v1/ai/image-to-video/runway-4-5" \
  -H "x-freepik-api-key: $FREEPIK_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "image": "https://raw.githubusercontent.com/meywd/tamma/main/docs/video/scenes/deep-dive/01-developer-burnout.png",
    "prompt": "Slow cinematic zoom into the developer figure at the desk. The 60% waste panel pulses with a dim red glow as warning icons drift and rotate lazily. The camera pushes forward and slightly down. Subtle particle dust floats in the air. Dark ambient lighting.",
    "duration": 8,
    "ratio": "1280:720",
    "seed": 42
  }'

# Poll for completion
curl -X GET "https://api.freepik.com/v1/ai/image-to-video/runway-4-5/$TASK_ID" \
  -H "x-freepik-api-key: $FREEPIK_API_KEY"

# Download completed video
curl -o clips/deep-dive/scene01-clip01.mp4 "$VIDEO_URL"
```

### Audio Narration: ElevenLabs Text-to-Speech API

All narration is generated via ElevenLabs TTS. Each scene's narration is generated as a separate audio file with per-scene voice settings.

- **Endpoint**: `POST https://api.elevenlabs.io/v1/text-to-speech/{voice_id}`
- **Auth Header**: `xi-api-key: <API_KEY>`
- **Voice**: George -- "Warm, Captivating Storyteller" (British, middle-aged, narrative)
  - **Voice ID**: `JBFqnCBsd6RMkjVDRZzb`
  - Selected for: warm authority, storytelling cadence, professional tech-narrator tone
- **Request Body**:
  - `text` (string, required): The narration text for the scene
  - `model_id` (string): `"eleven_multilingual_v2"`
  - `voice_settings` (object):
    - `stability` (float): Per-scene value (see each scene's Voice Settings)
    - `similarity_boost` (float): Per-scene value
    - `style` (float): Per-scene value
    - `speed` (float): Per-scene value
- **Response**: Binary audio stream (`audio/mpeg`)
- **Output Format**: `mp3_44100_128` (44.1kHz, 128kbps MP3)

#### Per-Scene Generation Pattern

```bash
# Example: Scene 1 narration
curl -X POST "https://api.elevenlabs.io/v1/text-to-speech/JBFqnCBsd6RMkjVDRZzb" \
  -H "xi-api-key: $ELEVENLABS_API_KEY" \
  -H "Content-Type: application/json" \
  -o audio/deep-dive/scene01-narration.mp3 \
  -d '{
    "text": "Development teams spend 40 to 60 percent of their time on repetitive toil. Writing boilerplate tests. Fixing linting errors. Coordinating CI/CD pipelines. Addressing the same review comments week after week. It is a tax on every team that ships software.",
    "model_id": "eleven_multilingual_v2",
    "voice_settings": {
      "stability": 0.72,
      "similarity_boost": 0.78,
      "style": 0.35,
      "speed": 0.92
    }
  }'
```

### Sound Effects: ElevenLabs Sound Generation API

Transition swooshes, ambient textures, and UI sounds are generated via ElevenLabs SFX.

- **Endpoint**: `POST https://api.elevenlabs.io/v1/sound-generation`
- **Auth Header**: `xi-api-key: <API_KEY>`
- **Request Body**:
  - `text` (string, required): Description of the desired sound effect
  - `duration_seconds` (float, optional): Target duration
- **Response**: Binary audio stream (`audio/mpeg`)

#### Sound Effect Inventory

```bash
# Transition swooshes
curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: $ELEVENLABS_API_KEY" \
  -H "Content-Type: application/json" \
  -o audio/deep-dive/sfx/swoosh-crossfade.mp3 \
  -d '{"text": "Smooth digital crossfade swoosh, subtle and clean", "duration_seconds": 0.5}'

curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: $ELEVENLABS_API_KEY" \
  -H "Content-Type: application/json" \
  -o audio/deep-dive/sfx/swoosh-wipeleft.mp3 \
  -d '{"text": "Energetic left-to-right wipe swoosh with slight reverb", "duration_seconds": 0.7}'

curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: $ELEVENLABS_API_KEY" \
  -H "Content-Type: application/json" \
  -o audio/deep-dive/sfx/swoosh-slideleft.mp3 \
  -d '{"text": "Quick slide transition sound, digital interface movement", "duration_seconds": 0.4}'

# Ambient textures (per-scene, generated from each scene's SFX Prompt below)
# See individual scene SOUND sections for specific prompts

# Common UI sounds
curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: $ELEVENLABS_API_KEY" \
  -H "Content-Type: application/json" \
  -o audio/deep-dive/sfx/success-chime.mp3 \
  -d '{"text": "Satisfying digital success chime, clean and bright", "duration_seconds": 1.0}'

curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: $ELEVENLABS_API_KEY" \
  -H "Content-Type: application/json" \
  -o audio/deep-dive/sfx/error-buzz.mp3 \
  -d '{"text": "Soft digital error buzz, not harsh, subtle warning tone", "duration_seconds": 0.8}'

curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: $ELEVENLABS_API_KEY" \
  -H "Content-Type: application/json" \
  -o audio/deep-dive/sfx/merge-complete.mp3 \
  -d '{"text": "Triumphant merge completion tone, warm and satisfying digital chime", "duration_seconds": 1.2}'

curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: $ELEVENLABS_API_KEY" \
  -H "Content-Type: application/json" \
  -o audio/deep-dive/sfx/deep-resonant-tone.mp3 \
  -d '{"text": "Deep resonant bass tone, regal and authoritative, warm golden feeling", "duration_seconds": 2.0}'
```

### Post-Production: ffmpeg

All stitching, transitions, audio mixing, and final render use ffmpeg.

- **Intra-scene clip concatenation**: `concat` filter (no transitions between clips within a scene)
- **Inter-scene transitions**: `xfade` filter (crossfade, wipeleft, slideleft per transition map)
- **Audio mixing**: Narration track + SFX layers mixed with `amix` / `adelay` filters
- **Upscale**: `scale=1920:1080` (Runway outputs 1280x720, upscaled to 1080p)
- **Final codec**: H.264 video (`libx264 -preset slow -crf 18`), AAC audio (`-c:a aac -b:a 192k`)

---

## SECTION A: THE PROBLEM (Scenes 1-3, ~56s)

---

### Scene 1: The 60% Tax

**Duration**: 16 seconds | **Clips**: 2

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/01-developer-burnout.png`
- **Description**: Dark tech-noir illustration with split composition. Left side dominates with a large coral-red holographic panel labeled "WASTED TIME" showing "60%" in bold, filled with warning triangles, lint icons, loading spinners, repeat arrows, comment bubbles, and question marks. Right side shows a tiny green bar labeled "ACTUAL FEATURES" with a lightbulb icon, reading only "10%". A small developer figure sits at a desk at the bottom center, dwarfed by the overwhelming waste panel. Dark navy background with subtle circuit-board grid lines.

**Frame B (Mid Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/01B-developer-burnout.png`
- **Description**: Closer, more intimate view. A developer sits at a desk with a green monitor, seen from behind. Surrounding them in concentric spiraling rings are glowing coral-red toil icons: warning triangles, gears, comment bubbles, repeat arrows, loading spinners, lint markers, and wrenches. The rings create a vortex-tunnel effect, trapping the developer in an endless cycle. Dark background with radial depth.

**Frame C (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/01C-developer-burnout.png`
- **Description**: Split panel with a heavy black weight icon pressing down from above in red glow. Left side shows a large block labeled "Repetitive Toil" packed with tiny gray workflow icons. Right side shows a much smaller green-bordered panel labeled "Features Shipped" with a stopwatch showing "00:00". Bottom shows a dollar sign and a "COST" line graph trending upward. The mood is oppressive -- toil crushes productivity.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame B
- **Duration**: 8 seconds
- **Motion Prompt**: "Slow cinematic zoom into the developer figure at the desk. The 60% waste panel pulses with a dim red glow as warning icons drift and rotate lazily. The camera pushes forward and slightly down, transitioning from the wide data-visualization view into a closer, more intimate tunnel perspective around the developer. Subtle particle dust floats in the air. Dark ambient lighting."
- **Camera**: Slow push-in zoom, slight downward tilt, steady dolly
- **Output**: `clips/deep-dive/scene01-clip01.mp4`

**Clip 2**: Frame B --> Frame C
- **Duration**: 8 seconds
- **Motion Prompt**: "The spiraling icons around the developer compress and flatten, transforming into the heavy weight pressing down. The camera pulls back and rises, revealing the cost graph climbing upward. The green 'Features Shipped' panel shrinks as the 'Repetitive Toil' block expands. The mood shifts from claustrophobic to oppressive. Red glow intensifies."
- **Camera**: Pull-back with slight upward crane, widening field of view
- **Output**: `clips/deep-dive/scene01-clip02.mp4`

#### NARRATION

**Text**:
> "Development teams spend **40 to 60 percent** of their time on repetitive toil. ... Writing boilerplate tests. Fixing linting errors. Coordinating CI/CD pipelines. Addressing the **same review comments** ... week after week. It is a **tax** on every team that ships software."

**Delivery**: Weary, matter-of-fact opening. Start low and measured, building slight frustration. The word "tax" lands with weight.

**Voice Settings**:
- stability: 0.72
- similarity_boost: 0.78
- style: 0.35
- speed: 0.92

**Timing Sync**:
- 0.0s-3.0s: "Development teams spend 40 to 60 percent..." -- camera begins zoom on the 60% panel
- 3.0s-7.0s: "...Writing boilerplate tests. Fixing linting errors..." -- icons pulse in sync with each item mentioned
- 7.0s-11.0s: "...Coordinating CI/CD pipelines..." -- transition to Frame B, developer surrounded by icons
- 11.0s-16.0s: "...same review comments week after week. It is a tax..." -- weight crushes down in Frame C

#### EMOTIONAL ARC

- **Start**: Weary recognition -- "Yes, I know this feeling"
- **End**: Frustrated agreement -- "This IS a tax on my team"

#### TRANSITIONS

- **Enter**: Fade from black (0.8s) -- cold open, no preamble
- **Exit**: Crossfade to Scene 2 (0.5s) -- smooth continuation of problem statement

#### SOUND

- Ambient: Low electronic hum, subtle mechanical clicking (keyboard ambiance), faint warning chime on the "60%" reveal
- SFX Prompt (ElevenLabs): `{"text": "Quiet office ambiance with soft electronic undertone, muted keyboard sounds, no music", "duration_seconds": 16.0}`

---

### Scene 2: Autocomplete Is Not Autonomy

**Duration**: 24 seconds | **Clips**: 3

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/02-autocomplete-not-autonomy.png`
- **Description**: Seven disconnected pipeline steps arranged horizontally as translucent glass-like rounded rectangles: ISSUE, PLAN, CODE, TEST, PR, FIX, MERGE. Between each step, red X marks sit on dashed lines, indicating broken connections. Above each step float small tool icons (magnifying glass, flowchart, keyboard, target, shield, wrench, merge arrows). The overall composition is fragmented -- each step is an island. Dark navy background with subtle green matrix-style digital rain and circuit traces.

**Frame B (Mid Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/02B-autocomplete.png`
- **Description**: Close-up dramatic view of a gap between two broken platform edges. Left edge labeled "Code" in blue neon with a cursor/click icon. Right edge labeled "Test" in blue neon. Between them, a large red X on a dashed line floats above a chasm labeled "The Gap" in a gold/amber badge. Below the gap, small icons (keyboard, alarm clock, hand) suggest manual effort needed to bridge it. Cracked textures on the platform edges. Cables and wires hang loosely.

**Frame C (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/02C-autocomplete.png`
- **Description**: Wide panoramic view of the full broken pipeline at an isometric angle. Seven hexagonal platform nodes (ISSUE, PLAN, CODE, TEST, PR, FIX, MERGE) float in space with large red X marks crashing between them, sparking with red electrical energy. A purple dashed line weaves chaotically between the nodes, unable to connect them. Above each node, holographic icon projections (bug, keyboard, flask, clipboard, wrench, rocket). A gold question mark glows at the far right end. The mood is one of chaos and disconnection.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame B
- **Duration**: 8 seconds
- **Motion Prompt**: "Camera tracks along the broken pipeline from left to right, passing each disconnected step. As the camera passes ISSUE and PLAN, the X marks between them flash red briefly. The camera accelerates slightly and zooms into the gap between CODE and TEST, pushing through the dashed line into a dramatic close-up of the chasm. Sparks fly from the broken edges. The cursor icon on the Code side blinks helplessly. Dark cinematic lighting with neon accents."
- **Camera**: Lateral tracking shot left-to-right, then push-in zoom into the CODE-TEST gap
- **Output**: `clips/deep-dive/scene02-clip01.mp4`

**Clip 2**: Frame B --> Frame C
- **Duration**: 8 seconds
- **Motion Prompt**: "Camera pulls back and rises from the close-up gap view, rotating slightly to reveal the full isometric panorama of all seven broken nodes. Red electrical sparks cascade between the X marks. The purple dashed line attempts to connect nodes but keeps breaking apart. Holographic icons above each node flicker. The gold question mark at the end pulses with uncertainty. The camera settles into a wide establishing shot showing the complete fragmentation."
- **Camera**: Pull-back crane rising to wide isometric overview, slight rotation
- **Output**: `clips/deep-dive/scene02-clip02.mp4`

**Clip 3**: Hold on Frame C with subtle motion
- **Duration**: 8 seconds
- **Motion Prompt**: "The full isometric panorama holds with all seven broken nodes visible. Red sparks continue cascading between the X marks. The purple dashed line makes one more attempt to connect the nodes, weaving chaotically before breaking apart again. The gold question mark at the far right pulses brighter with uncertainty. Each hexagonal node's holographic icon projection flickers independently. The camera drifts slowly inward, emphasizing the chaos and disconnection. The mood is one of frustrated incompleteness."
- **Camera**: Slow drift-in on the panoramic view, emphasizing fragmentation
- **Output**: `clips/deep-dive/scene02-clip03.mp4`

#### NARRATION

**Text**:
> "Existing AI dev tools help with **pieces** of the puzzle. ... Copilot autocompletes your code. ChatGPT answers questions. But **none of them** own the entire workflow. None of them can take an issue, plan the work, write the code, run the tests, create the PR, fix the failures, and merge it -- **end to end**. ... That gap ... is where all the toil lives."

**Delivery**: Conversational start, building to an accusation. The list of steps (issue, plan, code, test, PR, fix, merge) is delivered as a rapid cascade. "End to end" is punched. The final line "that gap is where all the toil lives" lands as a revelation.

**Voice Settings**:
- stability: 0.70
- similarity_boost: 0.78
- style: 0.40
- speed: 0.95

**Timing Sync**:
- 0.0s-5.0s: "Existing AI dev tools help with pieces... Copilot autocompletes your code. ChatGPT answers questions." -- camera tracks past the first few disconnected steps
- 5.0s-10.0s: "But none of them own the entire workflow. None of them can take an issue, plan the work, write the code..." -- rapid cascade as camera zooms into the gap
- 10.0s-17.0s: "...run the tests, create the PR, fix the failures, and merge it -- end to end" -- pull-back reveals full broken panorama
- 17.0s-24.0s: "That gap is where all the toil lives" -- hold on wide shot, question mark pulses

#### EMOTIONAL ARC

- **Start**: Acknowledgment -- "Yes, I use those tools"
- **End**: Realization -- "Oh, they really don't cover the whole workflow"

#### TRANSITIONS

- **Enter**: Crossfade from Scene 1 (0.5s)
- **Exit**: Crossfade to Scene 3 (0.5s)

#### SOUND

- Ambient: Digital glitch sounds on each X mark reveal, subtle electrical crackle in the gap, hollow wind in the chasm
- SFX Prompt (ElevenLabs): `{"text": "Digital interface sounds, subtle electrical sparks, hollow ambient wind, no music", "duration_seconds": 24.0}`

---

### Scene 3: Fear of Autonomy

**Duration**: 16 seconds | **Clips**: 2

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/03B-fear-of-autonomy.png`
- **Description**: A dark, atmospheric scene with a large cracked neon-purple question mark dominating the upper center. Below it, three fear-cards float in a row: Left card in red border shows "500 Error" with a broken server icon and "breaking changes" label. Center card in amber/gold border shows a broken padlock and warning triangle icon with "security vulnerability" label. Right card in gray border shows an audit log table filled entirely with question marks and "no transparency" label. At the bottom center, a silhouetted figure in a hat and trenchcoat stands with arms raised in uncertainty/alarm, backlit by an ominous amber glow. Smoky clouds frame the edges. Dark navy background with circuit-board traces.

**Frame B (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/03C-fear-of-autonomy.png`
- **Description**: A cyberpunk city scene in rain. A young developer stands at the bottom center, looking up pensively at a large translucent purple shield with a glowing question mark. Above the shield, three floating holographic cards show threats: a skull icon (malware), a broken padlock (security), and a face scan (privacy). Below the shield, three unchecked checkbox items read "Transparency", "Control", and "Audit Trail" -- all empty, emphasizing what is missing. Neon city lights in the background. The mood is uncertain but searching for answers.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame B
- **Duration**: 8 seconds
- **Motion Prompt**: "The cracked question mark pulses with purple lightning as the three fear cards (breaking changes, security vulnerability, no transparency) slowly rotate toward the viewer. The camera drifts forward through smoke, the silhouetted figure's arms lower as the scene transitions. Rain begins to fall. The environment shifts from abstract dark void to a cyberpunk cityscape. The question mark morphs from cracked neon into a shield shape. Ominous ambient lighting."
- **Camera**: Slow forward drift through atmospheric smoke, environmental transition
- **Output**: `clips/deep-dive/scene03-clip01.mp4`

**Clip 2**: Hold on Frame B with subtle motion
- **Duration**: 8 seconds
- **Motion Prompt**: "Rain falls steadily on the cyberpunk cityscape. The shield with the question mark pulses gently. The three unchecked boxes (Transparency, Control, Audit Trail) glow faintly, each one highlighting briefly in sequence. The developer figure shifts weight slightly, looking between the checkboxes. Threat cards above the shield drift and rotate slowly. Neon reflections shimmer on wet surfaces. Contemplative, searching mood."
- **Camera**: Slow push-in on the developer and shield, very subtle dolly
- **Output**: `clips/deep-dive/scene03-clip02.mp4`

#### NARRATION

**Text**:
> "And there is a **deeper problem**. ... Teams **fear** autonomous systems. What if it makes a **breaking change**? What if it ships a **security vulnerability**? What if **no one knows** what it did? ... Without transparency and control ... autonomy is just **risk**."

**Delivery**: Slower, more serious. Each "What if" is a beat -- delivered as genuine fears the audience has. The final line "autonomy is just risk" is a quiet, definitive statement. Slight pause before "risk."

**Voice Settings**:
- stability: 0.75
- similarity_boost: 0.80
- style: 0.30
- speed: 0.88

**Timing Sync**:
- 0.0s-4.0s: "And there is a deeper problem. Teams fear autonomous systems." -- cracked question mark pulses
- 4.0s-8.0s: "What if it makes a breaking change?" -- red card highlights; "What if it ships a security vulnerability?" -- amber card highlights
- 8.0s-12.0s: "What if no one knows what it did?" -- audit log card highlights, transition to cyberpunk scene
- 12.0s-16.0s: "Without transparency and control, autonomy is just risk." -- checkboxes glow in sequence, hold on developer's contemplation

#### EMOTIONAL ARC

- **Start**: Genuine apprehension -- "These are real fears I have"
- **End**: Sobering clarity -- "Autonomy without guardrails IS dangerous"

#### TRANSITIONS

- **Enter**: Crossfade from Scene 2 (0.5s)
- **Exit**: Purple glow wipe to Scene 4 (0.7s) -- marks the pivot from problem to solution

#### SOUND

- Ambient: Thunder rumble, rain on glass, distant warning klaxon (very subtle), electrical crackle on the cracked question mark
- SFX Prompt (ElevenLabs): `{"text": "Distant thunder, light rain ambiance, faint electrical crackle, ominous low drone, no music", "duration_seconds": 16.0}`

---


## SECTION B: INTRODUCING TAMMA (Scenes 4-6, ~50s)

---

### Scene 4: Tamma: It Is Done

**Duration**: 16 seconds | **Clips**: 2

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/03-tamma-intro.png`
- **Description**: Centered composition with the Tamma logo -- a golden Arabic calligraphy "tamm" inside a circular badge surrounded by a glowing purple neon ring. Left side shows a backlog panel labeled "BACKLOG" with four issue cards stacked vertically: "#101 User Authentication Flow", "#102 API Rate Limiting Optimizations", "#103 Dashboard Data Visualization", "#104 Notification System Update". Right side shows a green "MERGED PR" card: "PR #205: Feature Integration & Deployment" with a green checkmark and 100% progress bar. Purple and green energy streams flow from the backlog through the logo to the merged PR. Dark navy background with circuit traces and digital particle effects.

**Frame B (Mid Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/04B-tamma-it-is-done.png`
- **Description**: An infinity-loop composition in vibrant purple. Left side shows two dark issue cards ("Issue 4A: Database Optimization - Critical", "Issue 4B: API Rate Limiting") being pulled into the loop. The Tamma "TA" monogram logo sits at the center of the infinity shape. Right side shows the issues emerging as green merged PR cards ("Database Optimization - MERGED", "API Fix - MERGED"). Purple sparkle particles trail along the infinity path. Dark navy background.

**Frame C (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/04C-tamma-it-is-done.png`
- **Description**: Wide cinematic composition. Left side shows a single issue card "Issue #408: Minor Edge Case" with a warning icon, connected by purple flowing lines to the Tamma logo/atom icon at center. Right side shows a stack of five merged PR cards (PR #401 through #405) with green checkmarks and "Merged" badges, cascading in a 3D stack. Above the stack, golden Arabic calligraphy "tamm" glows with warm gold aura and text "it is done" beneath. Dark warm background with golden light emanating from the calligraphy.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame B
- **Duration**: 8 seconds
- **Motion Prompt**: "The Tamma logo pulses with golden light. Issue cards from the backlog on the left slide inward toward the glowing purple ring. As they pass through the logo, they transform -- the purple ring energy wraps around them. The camera pushes into the logo, which expands into an infinity loop shape. Purple sparkle particles emerge as issues transform into merged PRs on the other side. The mood shifts from dark problem space to confident solution energy. Regal, premium lighting."
- **Camera**: Push-in zoom through the Tamma logo, expanding perspective
- **Output**: `clips/deep-dive/scene04-clip01.mp4`

**Clip 2**: Frame B --> Frame C
- **Duration**: 8 seconds
- **Motion Prompt**: "The infinity loop completes its cycle as merged PR cards stack up on the right side. The camera slowly pulls back and rotates slightly right, revealing the growing pile of completed work. The Tamma atom logo settles into the center. Golden Arabic calligraphy materializes above the merged PRs with a warm light bloom. The text 'it is done' fades in beneath. The mood is triumphant and assured. Gold and purple light fills the frame."
- **Camera**: Slow pull-back with slight rightward pan, settling into wide composition
- **Output**: `clips/deep-dive/scene04-clip02.mp4`

#### NARRATION

**Text**:
> "**Tamma** is an autonomous development platform that handles the **complete workflow**. ... The name comes from the Arabic word '**tamm**' -- meaning ... '**it is done, it is complete.**' Tamma takes issues from your backlog and delivers **merged pull requests**. Not suggestions. Not autocomplete. ... **Done.**"

**Delivery**: Confident, warm reveal. "Tamma" is spoken with pride. The Arabic origin "tamm" is pronounced clearly with brief reverence. "Done" at the end is definitive -- a full stop with gravitas.

**Voice Settings**:
- stability: 0.78
- similarity_boost: 0.82
- style: 0.45
- speed: 0.90

**Timing Sync**:
- 0.0s-4.0s: "Tamma is an autonomous development platform that handles the complete workflow." -- logo pulses, issues begin flowing in
- 4.0s-8.0s: "...The name comes from the Arabic word 'tamm' -- meaning 'it is done, it is complete.'" -- infinity loop forms, calligraphy begins to glow
- 8.0s-13.0s: "Tamma takes issues from your backlog and delivers merged pull requests. Not suggestions. Not autocomplete." -- PRs stack up on right side
- 13.0s-16.0s: "Done." -- gold calligraphy fully revealed, warm light bloom, hold

#### EMOTIONAL ARC

- **Start**: Curiosity awakened -- "What IS this solution?"
- **End**: Confidence and intrigue -- "This sounds fundamentally different"

#### TRANSITIONS

- **Enter**: Purple glow wipe from Scene 3 (0.7s) -- dramatic section transition from Problem to Solution
- **Exit**: Crossfade to Scene 5 (0.5s)

#### SOUND

- Ambient: Deep resonant tone on logo reveal, subtle chime when "tamm" is spoken, satisfying merge/complete sound when "Done" lands
- SFX Prompt (ElevenLabs): `{"text": "Deep resonant drone transitioning to warm golden tone, subtle crystalline chimes, regal atmosphere, no music", "duration_seconds": 16.0}`

---

### Scene 5: End-to-End Autonomy

**Duration**: 18 seconds | **Clips**: 2

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/04-fourteen-step-pipeline.png`
- **Description**: A circular pipeline diagram with 14 numbered step nodes arranged clockwise like a clock face. Each node is a rounded square with a number and icon: 1 (ticket), 2 (search), 3 (lightbulb), 4 (blueprint), 5 (code brackets), 7 (test flask), 9 (upload arrow), 10 (eye), 12 (checkmark), 13 (merge arrows -- green glow), 14 (continue arrow). Purple energy pulses flow clockwise along the ring connecting them. Center text reads "< 2 hours" in bold white. Step 13 glows bright green (merge step). Dark navy background with circuit-board pattern. Some step numbers (6, 8, 11) are less visible but present on the ring.

**Frame B (Mid Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/05B-end-to-end-autonomy.png`
- **Description**: An arc-style pipeline showing the first five steps in detail along the top of a curved track. Step 1: "ADD OAUTH ENDPOINT" (ticket icon), Step 2: "CODE ANALYSIS" (magnifying glass), Step 3: lightbulb with tooltip "Define Scope, Setup Provider, Integrate Flow", Step 4: "DESIGN BLUEPRINT" (grid/schematic icon), Step 5: "TEST-FIRST SPECS" (checklist icon). Along the bottom of the arc, smaller nodes continue: 6 (Backlog), 7 (Development), 8 (Code Review), 9 (unlabeled), 10 (Merge), 11 (Build), 12 (unlabeled), 13 (Monitor), 14 (Feedback). Teal/cyan glow along the arc. Dark background.

**Frame C (Mid-Late Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/05C-end-to-end-autonomy.png`
- **Description**: Detailed view of the execution/review phase (steps 6-13). Shows: step 8 "upload" with an arrow, steps 6-7 as interlocking gears labeled "build", "test", "lint" with an "ALL GREEN" badge. Step 10 shows an eye icon with a "Review Comment" code snippet popup. Step 11 shows a wrench "FIX" with "Addressed review" note. Step 12 shows a large purple checkmark with a "VERIFICATION" card listing "Security Scan Passed", "Coverage Test Passed", "Compliance Check Passed". Step 13 glows bright green with a merge icon and "Merged" text. Branch label "feat/oauth-endpoint" at bottom. Purple and green tones throughout.

**Frame D (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/05D-end-to-end-autonomy.png`
- **Description**: Complete circular pipeline view as a mechanical conveyor-belt ring. All 14 steps are green glowing nodes connected by metallic track segments. Center shows "< 2 HOURS" in bold white with a clock/timer icon. Top right badge reads "CYCLE 2 - CONTINUOUS OPERATION". Step 1 "ISSUE CREATION" at the top has a translucent card above it. Step 14 "DEPLOYMENT & MONITORING" connects back to step 1 with a green arrow, emphasizing the continuous loop. The ring has a sci-fi industrial feel with rivets and connectors. Dark navy background.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame C
- **Duration**: 10 seconds
- **Motion Prompt**: "The circular 14-step pipeline rotates slowly clockwise. The camera zooms into the upper-right arc (steps 1-5), and the nodes expand from simple numbered squares into detailed labeled stations. Each station lights up in sequence with a brief pulse: ticket, analysis, lightbulb, blueprint, test specs. The arc unfolds from a circle into a curved track. The camera continues tracking along the pipeline, moving into the execution steps (6-13). Gears begin turning at the build/test station. An eye icon opens for the review step. The verification card slides in with checkmarks. The merge icon at step 13 ignites with bright green light. Purple energy trails behind the activation pulses."
- **Camera**: Clockwise rotation, zoom into upper arc, then lateral tracking through execution steps
- **Output**: `clips/deep-dive/scene05-clip01.mp4`

**Clip 2**: Frame C --> Frame D
- **Duration**: 8 seconds
- **Motion Prompt**: "The camera pulls back from the merge step, and the pipeline closes into a complete ring. All 14 nodes light up green simultaneously. The center text '< 2 HOURS' pulses once. The 'CYCLE 2' badge materializes in the corner. The loop arrow from step 14 back to step 1 glows, showing the continuous nature. The ring rotates slowly, giving it a perpetual-motion machine feel. The mood is one of impressive completeness."
- **Camera**: Pull-back to wide shot, ring settling into full view, slow rotation
- **Output**: `clips/deep-dive/scene05-clip02.mp4`

#### NARRATION

**Text**:
> "Tamma operates a **14-step autonomous loop**. Issue assignment. Context analysis. Planning. Design. Code generation following test-driven development. Build. Test execution. Push. CI/CD checks. Automated code review. Address review comments. Completion verification. Merge. ... And then it **picks the next issue**. The whole cycle completes in **under two hours** for a standard feature."

**Delivery**: Rapid-fire enumeration of the 14 steps builds energy. Each step name is crisp and staccato. Slow down on "picks the next issue" for emphasis. "Under two hours" is the payoff -- spoken with satisfied confidence.

**Voice Settings**:
- stability: 0.68
- similarity_boost: 0.78
- style: 0.50
- speed: 1.02

**Timing Sync**:
- 0.0s-3.0s: "Tamma operates a 14-step autonomous loop. Issue assignment. Context analysis." -- circular view, zoom begins
- 3.0s-7.0s: "Planning. Design. Code generation following TDD." -- steps 1-5 light up in sequence
- 7.0s-12.0s: "Build. Test execution. Push. CI/CD checks. Automated code review. Address review comments." -- rapid fire through execution steps, gears turn, eye opens
- 12.0s-14.0s: "Completion verification. Merge." -- green merge glow, verification card
- 14.0s-18.0s: "And then it picks the next issue. Under two hours." -- full ring view, < 2 hours text pulses

#### EMOTIONAL ARC

- **Start**: Impressed curiosity -- "14 steps? That's comprehensive"
- **End**: Awe at completeness -- "Under two hours, fully automated?"

#### TRANSITIONS

- **Enter**: Crossfade from Scene 4 (0.5s)
- **Exit**: Crossfade to Scene 6 (0.5s)

#### SOUND

- Ambient: Mechanical clicking as each step activates, subtle gear sounds at build/test, satisfying "ding" at merge, soft whoosh on the loop completion
- SFX Prompt (ElevenLabs): `{"text": "Mechanical precision sounds, soft clicks in rhythm, gear whir, satisfying completion chime, no music", "duration_seconds": 18.0}`

---

### Scene 6: You Stay in Control

**Duration**: 16 seconds | **Clips**: 2

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/06B-you-stay-in-control.png`
- **Description**: A developer silhouette stands center-right, gesturing toward a large holographic "Proposed Design" panel on the left. The design panel shows a component architecture diagram: Component A, Component B, and Component C connected via I/O lines to an "API Gate" node. In the upper right, a horizontal workflow pipeline shows the "Design Phase" at step 5 with a large amber pause icon, indicating the workflow has paused for human input. Below the developer, a gold "Your decision required" badge leads to two action buttons: a green "Approve" button with checkmark and a gray "Request Changes" button. The mood is empowering -- the human is in command.

**Frame B (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/06C-you-stay-in-control.png`
- **Description**: A grand architectural scene showing a massive ornate gateway/door with a thumbs-up icon, glowing green from behind. Silhouetted team members stand before it with arms raised in approval. Beyond the gateway, a "DEPLOYMENT PIPELINE" stretches into a cyberpunk cityscape with green neon pipes, data packets flowing through them, and envelope icons. The scene conveys that human approval is the gateway to production. Purple and green neon lighting. Dark navy background. The mood is triumphant and reassuring.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame B
- **Duration**: 8 seconds
- **Motion Prompt**: "The developer reaches toward the green 'Approve' button. As they press it, the button glows brightly and the pause icon in the workflow pipeline transforms into a play icon. A pulse of green energy radiates outward from the approval. The component design panel folds away. The camera pushes forward through the green energy wave, which transforms into the grand gateway opening. Light pours through the gateway doors as they swing open to reveal the deployment pipeline beyond. Empowering, decisive moment."
- **Camera**: Push-in through the approval action, transitioning through green energy to the gateway reveal
- **Output**: `clips/deep-dive/scene06-clip01.mp4`

**Clip 2**: Hold on Frame B with subtle motion
- **Duration**: 8 seconds
- **Motion Prompt**: "The gateway doors stand fully open, green light streaming through. Data packets flow through the deployment pipeline beyond, racing along neon pipes toward the city skyline. Team silhouettes lower their arms and observe the flow. Subtle rain falls. The thumbs-up icon pulses gently. The pipeline hums with activity. The camera slowly rises, showing the scale of what was unlocked by a single human decision. Confident, controlled atmosphere."
- **Camera**: Slow crane upward, widening to show the full deployment pipeline activity
- **Output**: `clips/deep-dive/scene06-clip02.mp4`

#### NARRATION

**Text**:
> "But this is **not a black box**. Tamma keeps you **in control** where it matters. ... You approve the design. You review breaking changes. You decide when to deploy to production. ... Tamma handles the **toil**. You make the decisions that require **human judgment**."

**Delivery**: Reassuring, authoritative. "Not a black box" directly answers the fear from Scene 3. "You" is emphasized each time -- this is about the viewer's agency. "Human judgment" at the end is spoken with respect.

**Voice Settings**:
- stability: 0.75
- similarity_boost: 0.80
- style: 0.40
- speed: 0.92

**Timing Sync**:
- 0.0s-4.0s: "But this is not a black box. Tamma keeps you in control where it matters." -- developer stands before the design panel, pause icon visible
- 4.0s-8.0s: "You approve the design. You review breaking changes." -- developer presses Approve, gateway begins opening
- 8.0s-12.0s: "You decide when to deploy to production." -- gateway fully open, deployment pipeline revealed
- 12.0s-16.0s: "Tamma handles the toil. You make the decisions that require human judgment." -- wide shot, pipeline active, team observes

#### EMOTIONAL ARC

- **Start**: Relief -- "Oh good, I'm still in control"
- **End**: Empowerment -- "This is exactly the right balance"

#### TRANSITIONS

- **Enter**: Crossfade from Scene 5 (0.5s)
- **Exit**: Slide left to Scene 7 (0.4s) -- section transition to "How It Works"

#### SOUND

- Ambient: Satisfying click on the Approve button, gateway creak/hydraulic opening sound, flowing data whoosh through pipes, ambient city hum
- SFX Prompt (ElevenLabs): `{"text": "Mechanical click, hydraulic door opening, flowing water/data whoosh, distant city ambiance, no music", "duration_seconds": 16.0}`

---


## SECTION C: HOW IT WORKS (Scenes 7-12, ~122s)

---

### Scene 7: Any AI, Your Choice

**Duration**: 20 seconds | **Clips**: 2

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/05-multi-provider-ai.png`
- **Description**: A three-tier architecture diagram. Top tier: eight circular provider icons in a row (Provider A through H), each with a distinct geometric symbol and unique neon color (cyan cube, pink swirl, orange molecules, green arrow, white polyhedron, blue snowflake, teal atom, green circle). Middle tier: a wide horizontal "Abstraction Layer" bar glowing bright purple with digital noise texture. Bottom tier: four task boxes labeled "Analysis" (chart icon), "Code Gen" (code brackets), "Review" (checkmark), and "Testing" (chart icon). Colored routing lines flow from providers through the abstraction layer to specific tasks, showing different providers assigned to different tasks. Dark navy background with circuit traces.

**Frame B (Mid Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/07B-any-ai-your-choice.png`
- **Description**: A more detailed routing view with five provider circles at top (Provider A green, C purple, B cyan/bright, D maroon, E orange) connected by thick neon-colored pipes through an "ABSTRACTION LAYER" horizontal bar. Below the bar, five task labels: ANALYSIS, TESTING, CODE GEN, DEPLOYMENT, REVIEW. Provider B (brightest, center) routes to Code Gen with a prominent blue beam. A "Cost: -25%" badge sits near the center, highlighting cost optimization. Diamond-shaped routing nodes sit at junction points. The routing shows cross-connections -- Provider A routes to Analysis and Testing, Provider E routes to Review. Clean, architectural feel.

**Frame C (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/07C-any-ai-your-choice.png`
- **Description**: An isometric circuit-board view showing a fallback scenario in action. Provider B's circle glows red with a stop icon and a "BREAKER TRIPPED" label with a broken plug icon. A dashed "REROUTING..." path diverts from Provider B to Provider C (glowing cyan) below. Provider C connects to the "Code Gen" task box via a green "Fallback: Active" badge. A small latency monitor in the corner shows "Latency: 45ms (Normal)" with a graph. Other task labels (Database, Analytics, User Interface) are dimmed in the background. The mood is one of resilience -- the system self-heals.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame B
- **Duration**: 10 seconds
- **Motion Prompt**: "Eight provider circles at the top light up one by one from left to right, each with its distinct color. As they activate, routing lines shoot downward through the Abstraction Layer bar with a pulse of purple light. The camera slowly pushes in, and the view shifts to show five providers in more detail with thicker pipe connections. The 'Cost: -25%' badge fades in at center. Provider B's connection to Code Gen intensifies with a bright blue beam. Data particles flow along the colored pipes. Technical, impressive, flowing animation."
- **Camera**: Slow push-in zoom, providers activating in sequence
- **Output**: `clips/deep-dive/scene07-clip01.mp4`

**Clip 2**: Frame B --> Frame C
- **Duration**: 10 seconds
- **Motion Prompt**: "Provider B's circle flickers and turns red. A circuit breaker icon snaps shut with a spark. The 'BREAKER TRIPPED' label appears. The routing line from Provider B to Code Gen breaks apart with small particles. After a beat, a new dashed path reroutes around Provider B, flowing down to Provider C which brightens from dim to full cyan glow. The 'Fallback: Active' badge slides in. The latency monitor updates in real time showing stable performance. The camera shifts to an isometric angle showing the self-healing in action. The mood shifts from momentary concern to confident resilience."
- **Camera**: Transition from front-on to isometric view, following the rerouting path
- **Output**: `clips/deep-dive/scene07-clip02.mp4`

#### NARRATION

**Text**:
> "Tamma supports **eight-plus AI providers** through a unified abstraction layer. Anthropic Claude, OpenAI, Google Gemini, OpenRouter, OpenCode, Zen MCP, local LLMs -- even your own fine-tuned models. The system **intelligently routes** tasks to the best provider for each step. Code generation might use Claude. Review might use GPT-4. **Cost optimization** reduces spend by 20 to 30 percent."

**Delivery**: Technical and authoritative. The provider list is delivered smoothly without rushing. "Intelligently routes" is emphasized. "Cost optimization" is the practical payoff. Confident, knowledgeable tone throughout.

**Voice Settings**:
- stability: 0.72
- similarity_boost: 0.78
- style: 0.42
- speed: 0.95

**Timing Sync**:
- 0.0s-5.0s: "Tamma supports eight-plus AI providers through a unified abstraction layer." -- providers light up, abstraction bar glows
- 5.0s-10.0s: "Anthropic Claude, OpenAI, Google Gemini, OpenRouter, OpenCode, Zen MCP, local LLMs -- even your own fine-tuned models." -- provider names as pipes activate
- 10.0s-15.0s: "The system intelligently routes tasks to the best provider for each step. Code generation might use Claude. Review might use GPT-4." -- routing lines diverge, Code Gen/Review highlighted
- 15.0s-20.0s: "Cost optimization reduces spend by 20 to 30 percent." -- Cost badge appears, breaker trips and reroutes

#### EMOTIONAL ARC

- **Start**: Technical interest -- "This is vendor-agnostic, nice"
- **End**: Practical appreciation -- "Smart routing AND cost savings"

#### TRANSITIONS

- **Enter**: Slide left from Scene 6 (0.4s) -- section start
- **Exit**: Crossfade to Scene 8 (0.5s)

#### SOUND

- Ambient: Soft electronic activation tones as providers light up, data flow whoosh through pipes, sharp snap on circuit breaker trip, satisfying reconnection tone on fallback
- SFX Prompt (ElevenLabs): `{"text": "Electronic activation sounds, data flow hum, sharp electrical snap, reconnection ping, no music", "duration_seconds": 20.0}`

---

### Scene 8: Every Git Platform

**Duration**: 16 seconds | **Clips**: 2

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/06-multi-platform-git.png`
- **Description**: "One Interface" text in white at the top. Below, seven platform cards arranged in two rows (4 top, 3 bottom): GitLab (fox icon, orange), GitHub (octocat icon, red), Bitbucket (bucket icon, blue-green), Gitea (teapot icon, green), Azure DevOps (diamond icon, yellow-green), SourceForge (hexagonal icon, red), Gogs (tree icon, green). All cards connect via bright green lines downward to a central glowing purple Tamma node. Below the Tamma node, a small configuration file icon. Clean, organized layout showing universality. Dark navy background.

**Frame B (Mid Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/08B-every-git-platform.png`
- **Description**: A dramatic perspective view showing the practical split. Left side: a large "OPEN SOURCE" panel (green, with git-branch icon and "15.2k" stars badge). Right side: a large "ENTERPRISE" panel (blue, with padlock icon). Behind them, smaller cards show various project types: Cloud Service, Mobile SDK, IoT Platform, AI Engine, Legacy System. All connect through a central "IGitPlatform" interface bar with bidirectional arrows. Below, a glowing purple Tamma portal with a "Same Config" hexagonal badge. The message: one interface handles both open source and enterprise, across all project types.

**Frame C (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/08C-every-git-platform.png`
- **Description**: A YAML configuration file view labeled "tamma-config.yaml" in a code editor window. The config shows: "platform:" with a list including "- name: 'google-cloud' # Active" (highlighted in green with a checkmark), "- name: 'aws'", "- name: 'azure'", "- name: 'docker'", "- name: 'kubernetes'", "- name: 'terraform'", "- name: 'ansible'". Platform icons float around the editor: AWS, Google Cloud, Azure (A), Docker (whale), Kubernetes (ship wheel), Terraform (T), Ansible (A). Tamma logo at bottom with "One Line Change" badge with a refresh icon. The message: switching platforms is a single config change.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame B
- **Duration**: 8 seconds
- **Motion Prompt**: "The seven platform cards light up in quick succession -- GitLab, GitHub, Bitbucket, Gitea, Azure DevOps, SourceForge, Gogs. Green connection lines pulse downward to the Tamma node. The camera pushes forward and the layout transforms from a flat grid into a dramatic 3D perspective view. The cards split into two groups: open source repos floating left, enterprise repos floating right. The IGitPlatform interface bar materializes between them. The 'Same Config' badge fades in below. Smooth, technical transformation."
- **Camera**: Push-in zoom with perspective shift from flat to 3D depth
- **Output**: `clips/deep-dive/scene08-clip01.mp4`

**Clip 2**: Frame B --> Frame C
- **Duration**: 8 seconds
- **Motion Prompt**: "The Open Source and Enterprise panels merge toward the center, collapsing into a configuration file that expands to fill the frame. The YAML content types out line by line. The 'google-cloud' line highlights with a green glow and checkmark. Platform icons float out from each config line and orbit around the editor. A cursor moves to the active line. The Tamma logo appears at the bottom with the 'One Line Change' badge sliding in. The mood is one of simplicity -- all this complexity reduced to a config file."
- **Camera**: Transition from 3D scene to flat config view, settling into center frame
- **Output**: `clips/deep-dive/scene08-clip02.mp4`

#### NARRATION

**Text**:
> "Tamma is **not locked to GitHub**. It works with GitHub, GitLab, Gitea, Forgejo, Bitbucket, Azure DevOps, and plain Git. **One configuration, every platform.** ... Your team uses GitLab? **Done.** Your open-source project is on GitHub while your company uses Azure DevOps? Tamma handles **both**."

**Delivery**: Confident and practical. The platform list is delivered with casual authority. "One configuration, every platform" is the hook -- spoken clearly. "Done" mirrors the Tamma tagline. "Both" at the end is punched to show flexibility.

**Voice Settings**:
- stability: 0.72
- similarity_boost: 0.78
- style: 0.38
- speed: 0.95

**Timing Sync**:
- 0.0s-4.0s: "Tamma is not locked to GitHub. It works with GitHub, GitLab, Gitea, Forgejo, Bitbucket, Azure DevOps, and plain Git." -- platform cards light up in sequence
- 4.0s-8.0s: "One configuration, every platform." -- IGitPlatform interface bar appears, "Same Config" badge
- 8.0s-12.0s: "Your team uses GitLab? Done." -- config file appears, lines type out
- 12.0s-16.0s: "Your open-source project is on GitHub while your company uses Azure DevOps? Tamma handles both." -- "One Line Change" badge, platform icons orbit

#### EMOTIONAL ARC

- **Start**: Surprised appreciation -- "It's not just GitHub?"
- **End**: Practical relief -- "Our multi-platform setup is supported"

#### TRANSITIONS

- **Enter**: Crossfade from Scene 7 (0.5s)
- **Exit**: Crossfade to Scene 9 (0.5s)

#### SOUND

- Ambient: Quick activation pings as each platform lights up, smooth data flow sounds, gentle keyboard click as config types, subtle chime on "Done"
- SFX Prompt (ElevenLabs): `{"text": "Quick electronic pings, smooth data flow hum, keyboard typing sounds, subtle completion chime, no music", "duration_seconds": 16.0}`

---

### Scene 9: Mandatory Quality Gates

**Duration**: 18 seconds | **Clips**: 2

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/09B-quality-gates.png`
- **Description**: A dramatic side-by-side composition showing the retry-and-fix cycle. Left side: a red circular arrow loop with a large red X icon at center labeled "FAILED TEST". Above it, a red-tinted code snippet showing a function with an error (calling `calculate(undefined)`). Right side: a green circular arrow loop with a green checkmark labeled "RETESTED: PASS". Below it, a green-tinted code snippet showing the corrected function (calling `calculate(data)`). The red-to-green transition is visually striking. Dark navy background with circuit traces. The message: Tamma automatically diagnoses failures and fixes them.

**Frame B (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/09C-quality-gates.png`
- **Description**: An escalation scenario. Left side: a physical gate structure labeled "Gate 2 (Test)" with a doorway showing red X marks and "retry: 0" counter. An amber connection line arcs from the gate upward to a human figure (glowing blue/cyan) on the right. A card shows "Escalation: Test failure after 3 retries" with failing test code. Below the human, two action buttons: green "Fix & Retry" and red "Abort". At the bottom center, a large pause icon and a shield badge reading "Nothing Ships Broken". A workflow progress bar stretches across the top. The mood is one of controlled safety.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame B
- **Duration**: 10 seconds
- **Motion Prompt**: "The red FAILED TEST circle spins rapidly with sparks. The broken code snippet flashes. After a beat, the red circle slows and transforms -- the error code morphs into corrected code with a green highlight. The circle transitions from red to green as the RETESTED: PASS checkmark appears with a satisfying glow. Then the camera pulls back, revealing a physical gate structure. The retry counter ticks from 3 down to 0. On hitting 0, an amber escalation line shoots upward toward a human figure. The gate pauses. The mood transitions from automated recovery to human escalation."
- **Camera**: Hold on retry cycle, then pull-back to reveal gate and escalation path
- **Output**: `clips/deep-dive/scene09-clip01.mp4`

**Clip 2**: Hold on Frame B with subtle motion
- **Duration**: 8 seconds
- **Motion Prompt**: "The human figure examines the escalation card. The failing test code scrolls briefly. The 'Fix & Retry' and 'Abort' buttons glow, awaiting input. The pause icon pulses slowly. The 'Nothing Ships Broken' shield badge gleams. The workflow progress bar shows the pipeline paused at the Test gate. The camera slowly zooms into the 'Nothing Ships Broken' shield as the final statement. Confident, reassuring atmosphere. Circuit traces pulse softly in the background."
- **Camera**: Slow push-in toward the shield badge, centering on the safety promise
- **Output**: `clips/deep-dive/scene09-clip02.mp4`

#### NARRATION

**Text**:
> "Every change passes through **mandatory quality gates**. Build verification. Test execution with smart retry logic -- up to **three attempts** with intelligent fixes before escalation. Security scanning for vulnerabilities. Automated code review. These gates **cannot be bypassed**. If something fails three times, Tamma **escalates to a human**. ... **Nothing ships broken.**"

**Delivery**: Firm and authoritative. "Mandatory" and "cannot be bypassed" are delivered with conviction. "Three attempts" is precise. "Escalates to a human" is reassuring. "Nothing ships broken" is the scene's mic-drop moment -- delivered slowly and definitively.

**Voice Settings**:
- stability: 0.78
- similarity_boost: 0.82
- style: 0.35
- speed: 0.90

**Timing Sync**:
- 0.0s-5.0s: "Every change passes through mandatory quality gates. Build verification." -- failed test cycle spinning red
- 5.0s-10.0s: "Test execution with smart retry logic -- up to three attempts with intelligent fixes before escalation. Security scanning for vulnerabilities. Automated code review." -- red-to-green retry transformation
- 10.0s-15.0s: "These gates cannot be bypassed. If something fails three times, Tamma escalates to a human." -- gate structure, escalation path lights up, human figure appears
- 15.0s-18.0s: "Nothing ships broken." -- zoom to shield badge, pause icon pulses

#### EMOTIONAL ARC

- **Start**: Technical interest -- "How does it handle failures?"
- **End**: Deep trust -- "This is genuinely safe to use"

#### TRANSITIONS

- **Enter**: Crossfade from Scene 8 (0.5s)
- **Exit**: Crossfade to Scene 10 (0.5s)

#### SOUND

- Ambient: Spinning/whirring on the retry cycle, error buzz on failure, satisfying ping on green pass, alarm-like tone on escalation (not harsh), deep resonant tone on "Nothing ships broken"
- SFX Prompt (ElevenLabs): `{"text": "Mechanical spinning, soft error buzz, green success ping, subtle alarm tone, deep reassuring resonance, no music", "duration_seconds": 18.0}`

---

### Scene 10: Time-Travel Debugging

**Duration**: 24 seconds | **Clips**: 3

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/10B-time-travel-debugging.png`
- **Description**: A close-up of a single event card displayed on a DNA-strand-like timeline. The central card is labeled "TEST.FAILED" in red with a warning icon. It shows structured event data: "type: TEST.FAILED", "timestamp: 2025-11-15T14:23:56.789Z", "tags: {issueId: 'E-145-B782', provider: 'claude'}", "data: {failedTest: 'auth.test.ts', error: 'TimeoutError: Connection to database timed out after 5000ms. Retrying...'}". To the left, a smaller green card reads "CODE.GENERATED.SUCCESS" with 100% progress. To the right, a smaller green card reads "TEST.RETRIED.SUCCESS". The DNA double-helix strands (green and purple) weave through the cards. Bottom label: "Zoom Level: Event Detail". Dark background.

**Frame B (Mid Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/10C-time-travel-debugging.png`
- **Description**: A workflow timeline view labeled "SCENE 10A TIMELINE: WORKFLOW ORCHESTRATION". Steps flow left to right: START, CONTEXT ANALYSIS (brain icon), DEPENDENCY RESOLUTION (network icon), then a golden rewind/key icon at center representing the time-travel point. Beyond it: VALIDATION & TESTING, DEPLOYMENT READINESS, END: COMPLETE. Below the golden icon, a "STATE SNAPSHOT" card shows: "currentStep: CODE_GENERATION", "provider: claude" (with icon), "filesModified: auth.ts (saved), auth.test.ts" (green checkmark), "retryCount: 1". The golden icon represents the ability to reconstruct exact state at any point. Dark navy background with circuit traces.

**Frame C (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/10D-time-travel-debugging.png`
- **Description**: Three large compliance shield badges in a row. Left: a golden "SOC2 - Service Organization Control" shield with checkmark, labeled "Complete Audit Trail". Center: a silver/blue "ISO 27001 - Information Security Management" shield with checkmark, labeled "Millisecond Precision". Right: a green "GDPR - General Data Protection Regulation" shield with checkmark, labeled "Tamper-Proof". A purple DNA helix strand weaves behind the shields. Below, bold text reads "Immutable Event Stream". Circuit-board traces connect the shields. Dark navy background. Premium, authoritative mood.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame B
- **Duration**: 8 seconds
- **Motion Prompt**: "The detailed event card (TEST.FAILED) rotates slowly in 3D, showing its structured data. The camera pulls back from the event-detail zoom level, and the DNA timeline strands stretch outward. More event nodes appear along the timeline as the perspective widens. The view transitions from microscopic event detail to a macroscopic workflow timeline. The golden time-travel key icon materializes at center with a bright pulse. The state snapshot card slides in below it, showing the reconstructed state. The mood is one of powerful visibility."
- **Camera**: Pull-back zoom from event detail to workflow overview, centering on time-travel icon
- **Output**: `clips/deep-dive/scene10-clip01.mp4`

**Clip 2**: Frame B --> Frame C
- **Duration**: 8 seconds
- **Motion Prompt**: "The golden time-travel key turns and locks with a satisfying click. The workflow timeline folds downward and transforms into circuit-board traces. The three compliance shields rise from below, each appearing in sequence with a gleam: SOC2 (gold), ISO 27001 (silver/blue), GDPR (green). Their labels fade in below each shield. The DNA helix continues behind them as a visual thread. The 'Immutable Event Stream' text materializes at the bottom with weight. The mood shifts from technical power to institutional trust."
- **Camera**: Downward tilt transition, shields rising into frame in sequence
- **Output**: `clips/deep-dive/scene10-clip02.mp4`

**Clip 3**: Hold on Frame C with subtle motion
- **Duration**: 8 seconds
- **Motion Prompt**: "The three compliance shields gleam softly with light reflections moving across their surfaces. The DNA helix behind them rotates slowly. Circuit traces pulse with gentle light. The 'Immutable Event Stream' text glows. The camera very slowly pushes in on the three shields as the final establishing shot of this scene. Premium, trustworthy atmosphere. Subtle sparkle particles float."
- **Camera**: Very slow push-in, shields gleaming, particles floating
- **Output**: `clips/deep-dive/scene10-clip03.mp4`

#### NARRATION

**Text**:
> "Every action Tamma takes is recorded as an **immutable event** with millisecond precision. Who assigned the issue. What code was generated. Which tests failed. Who approved the deployment. This is not just logging -- it is a **complete audit trail**. You can reconstruct the **exact state** of any workflow at any point in time. ... Perfect for debugging. Essential for compliance. **SOC2, ISO 27001, GDPR** -- the evidence is **built in**."

**Delivery**: Technical gravitas. Start with measured authority describing the event recording. Build through the examples. "Not just logging" is a pivot -- spoken with emphasis. The compliance standards are delivered with weight. "Built in" at the end is definitive.

**Voice Settings**:
- stability: 0.75
- similarity_boost: 0.80
- style: 0.38
- speed: 0.90

**Timing Sync**:
- 0.0s-6.0s: "Every action Tamma takes is recorded as an immutable event with millisecond precision. Who assigned the issue. What code was generated. Which tests failed." -- event card rotating, pull-back to workflow timeline
- 6.0s-12.0s: "Who approved the deployment. This is not just logging -- it is a complete audit trail." -- golden key icon materializes, state snapshot visible
- 12.0s-18.0s: "You can reconstruct the exact state of any workflow at any point in time. Perfect for debugging. Essential for compliance." -- key locks, shields begin rising
- 18.0s-24.0s: "SOC2, ISO 27001, GDPR -- the evidence is built in." -- compliance shields fully visible, hold on final composition

#### EMOTIONAL ARC

- **Start**: Technical appreciation -- "Every action is tracked, nice"
- **End**: Institutional trust -- "This meets enterprise compliance requirements"

#### TRANSITIONS

- **Enter**: Crossfade from Scene 9 (0.5s)
- **Exit**: Crossfade to Scene 11 (0.5s)

#### SOUND

- Ambient: Subtle data stream hum, click/lock sound on the golden key, metallic gleam on each shield reveal, deep resonant bass on "Immutable Event Stream"
- SFX Prompt (ElevenLabs): `{"text": "Subtle electronic data flow, metallic click-lock, crystalline shield gleam sounds, deep bass undertone, no music", "duration_seconds": 24.0}`

---

### Scene 11: Visual Workflow Orchestration

**Duration**: 20 seconds | **Clips**: 2

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/11B-visual-workflow-orchestration.png`
- **Description**: An ELSA workflow visual designer interface. Toolbar at top with zoom, grid, and play controls. Breadcrumb reads "Project: ELSA Core > Workflow: Master Branch > Current Activity: Generate Plan". A flowchart shows connected nodes: "Analyze Code" (green checkmark, completed) connects to "Generate Plan" (currently running, with green progress bar and amber pause icon). Below, the flow branches to "Run Tests" and "Integrate Changes". A decision diamond "Code Approved?" branches to "No" (loops back) or "Yes" (continues to "Deploy to Staging" then "Monitor Performance" then another diamond "Performance Optimal?"). Status bar at bottom shows CPU: 45%, MEM: 3.2GB, LATENCY: 12ms. The feel is of a real, functional workflow designer tool.

**Frame B (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/11C-visual-workflow-orchestration.png`
- **Description**: An isometric 3D architectural view showing the dual-stack system. Left side: a "TYPESCRIPT STACK" tower with stacked layers labeled "AI Providers", "AI Providers", "Frontend", "Services", "Data Layer" in blue-purple tones. Center: a large "API GATEWAY" block with circuit-board detailing and a REST API label, connected by flowing data arrows. Right side: an "ELSA WORKFLOW ENGINE" tower with stacked workflow definition panels and an "Event Bus" component. Data flows from the TypeScript stack through the API Gateway to the ELSA engine. The architecture is impressive and industrial. Dark navy background with green accent lighting.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame B
- **Duration**: 10 seconds
- **Motion Prompt**: "The ELSA workflow designer shows the 'Generate Plan' node's progress bar filling up. The pause icon blinks. Flow paths illuminate as data moves through the flowchart -- from Analyze Code to Generate Plan, branching to Run Tests. The decision diamond 'Code Approved?' rotates slightly. The camera slowly pulls back from the 2D designer interface, and the flat workflow begins to extrude into 3D. The nodes transform from flat rectangles into the stacked architectural blocks of the dual-stack system. The transition reveals the engineering depth behind the visual simplicity."
- **Camera**: Pull-back from 2D interface view, transitioning to isometric 3D architecture
- **Output**: `clips/deep-dive/scene11-clip01.mp4`

**Clip 2**: Hold on Frame B with subtle motion
- **Duration**: 10 seconds
- **Motion Prompt**: "The isometric architecture hums with activity. Data particles flow from the TypeScript stack through the API Gateway to the ELSA Workflow Engine. The Event Bus pulses with message deliveries. Light traces flow along the connection arrows. The TypeScript stack layers glow in sequence from bottom to top. The ELSA engine workflows shuffle slightly as if being edited. The camera drifts slowly around the architecture, showing its depth and interconnection. Industrial, impressive, production-grade atmosphere."
- **Camera**: Slow orbital drift around the isometric architecture, showcasing depth
- **Output**: `clips/deep-dive/scene11-clip02.mp4`

#### NARRATION

**Text**:
> "Under the hood, Tamma uses a **dual-stack architecture**. TypeScript and Node.js power the AI providers, CLI, and API. ... Dotnet and the **ELSA workflow engine** handle orchestration -- **20-plus composable workflows** that are visual, pausable, and resumable. You can even design **custom workflows** in the ELSA visual studio. This is not a brittle script. It is a **production-grade workflow engine**."

**Delivery**: Technical respect and pride. "Dual-stack architecture" is delivered with engineering authority. "20-plus composable workflows" shows scale. "Production-grade workflow engine" is the closer -- spoken with conviction that this is serious infrastructure, not a toy.

**Voice Settings**:
- stability: 0.72
- similarity_boost: 0.78
- style: 0.42
- speed: 0.93

**Timing Sync**:
- 0.0s-5.0s: "Under the hood, Tamma uses a dual-stack architecture. TypeScript and Node.js power the AI providers, CLI, and API." -- workflow designer active, progress bar filling
- 5.0s-10.0s: "Dotnet and the ELSA workflow engine handle orchestration -- 20-plus composable workflows that are visual, pausable, and resumable." -- 2D-to-3D transition, ELSA tower materializes
- 10.0s-15.0s: "You can even design custom workflows in the ELSA visual studio." -- data flows through architecture, Event Bus pulses
- 15.0s-20.0s: "This is not a brittle script. It is a production-grade workflow engine." -- full architecture visible, camera orbiting, industrial mood

#### EMOTIONAL ARC

- **Start**: Technical curiosity -- "What's under the hood?"
- **End**: Engineering respect -- "This is serious, production-grade infrastructure"

#### TRANSITIONS

- **Enter**: Crossfade from Scene 10 (0.5s)
- **Exit**: Crossfade to Scene 12 (0.5s)

#### SOUND

- Ambient: Soft UI sounds from the workflow designer, progress bar filling tone, 3D extrusion whoosh, industrial hum for the architecture, data flow particles
- SFX Prompt (ElevenLabs): `{"text": "Soft interface clicks, progress tone, industrial architectural hum, data flow particles, no music", "duration_seconds": 20.0}`

---

### Scene 12: Intelligent Agent Routing

**Duration**: 24 seconds | **Clips**: 3

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/12B-intelligent-agent-routing.png`
- **Description**: Title: "AI ROLE ROUTING & FAILOVER SYSTEM". Left side: a "CONFIGURATION PANEL" showing YAML-like config with "# initialized", "yaml: code", "coder:", "provider:", "primary: claude-4" (green checkmark), "fallback_1: gpt-4o", "fallback_2: local-llama". Right side: a flow diagram showing a "Generate Code" task (green) flowing into a purple hexagonal "Role Resolver" node, which routes to three providers: "claude-4" (brain icon, primary path with green line), "gpt-4o" (gray, fallback), and "local-llama" (gray, fallback). A padlock/security gate sits between the Role Resolver and claude-4. The config directly controls the routing. Dark navy background with circuit traces and amber accent lights.

**Frame B (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/12C-intelligent-agent-routing.png`
- **Description**: A full diagnostics dashboard titled "SYSTEM DIAGNOSTICS: PROVIDER METRICS". Four quadrant panels: (1) "Cost per Provider" -- bar chart showing Provider Nexus-9 at $1.2M (red), Provider CyberCore at $450K (purple), Provider Synapse at $800K (green). (2) "Token Usage" -- horizontal progress bars showing Nexus-9 at 120K/200K, CyberCore at 180K/200K (amber warning), Synapse at 60K/100K. (3) "Latency" -- line graph with threshold at 50ms, showing Nexus-9 at 12ms, CyberCore at 24ms, Synapse at 18ms (all healthy/green). (4) "Error Rate" -- three circular gauges: Nexus-9 at 0.2% (green, "Stable"), CyberCore at 1.5% (amber, "Warning"), Synapse at 0% (green, "Optimal"). Top bar shows API Gateway, Load Balancer, Provider Routing indicators. Cyberpunk city skyline visible through a window behind the dashboard. Dark theme with neon accents.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame B
- **Duration**: 8 seconds
- **Motion Prompt**: "The YAML configuration panel's cursor highlights the 'primary: claude-4' line. The Role Resolver hexagon pulses purple and sends a routing beam to claude-4. The security gate opens briefly. The routing succeeds. Then the camera slides right, and the configuration panel transforms into a diagnostics dashboard. The four metric panels materialize one by one: Cost bars rise, Token bars fill, Latency lines draw, Error gauges spin to their values. Data updates in real-time. The mood transitions from configuration to observability."
- **Camera**: Slide-right transition from config view to dashboard view
- **Output**: `clips/deep-dive/scene12-clip01.mp4`

**Clip 2**: Hold on Frame B with subtle motion
- **Duration**: 8 seconds
- **Motion Prompt**: "The diagnostics dashboard is alive with real-time updates. Cost bars flicker slightly. Token usage bars creep upward. The latency line graph draws new data points, staying under the threshold. CyberCore's error rate gauge ticks from 1.4% to 1.5%, triggering an amber 'Warning' flash. The city skyline through the window shows flying vehicles and neon signs. The camera slowly zooms into the dashboard, emphasizing the level of observability. Professional, mission-control atmosphere."
- **Camera**: Slow push-in on the dashboard, data updating in real-time
- **Output**: `clips/deep-dive/scene12-clip02.mp4`

**Clip 3**: Hold on Frame B with continued motion
- **Duration**: 8 seconds
- **Motion Prompt**: "The dashboard continues its real-time updates. The CyberCore warning triggers a brief amber pulse across the panel. The cost bars settle, showing the Provider Nexus-9 at highest cost. Token usage for CyberCore creeps closer to its limit. The latency graph remains steady and healthy. The camera drifts slowly across the four quadrants, giving each metric panel a moment of focus. City lights reflect on the dashboard glass. The mood is one of complete operational visibility and control."
- **Camera**: Slow lateral drift across the dashboard quadrants
- **Output**: `clips/deep-dive/scene12-clip03.mp4`

#### NARRATION

**Text**:
> "Tamma uses a **config-driven multi-agent system**. Different workflow phases map to different agent roles -- planning, coding, reviewing, testing. Each role has an ordered **provider chain with fallbacks** and circuit breakers. If Claude is down, it falls back to GPT-4. If that is slow, it routes to a local model. ... **Diagnostics** track cost, tokens, latency, and errors per provider in **real time**."

**Delivery**: Technical and operational. Emphasis on the practical resilience ("If Claude is down..."). The fallback chain is delivered as a reassuring sequence. "Diagnostics" marks the shift to observability. "Real time" at the end is punched.

**Voice Settings**:
- stability: 0.72
- similarity_boost: 0.78
- style: 0.40
- speed: 0.95

**Timing Sync**:
- 0.0s-6.0s: "Tamma uses a config-driven multi-agent system. Different workflow phases map to different agent roles -- planning, coding, reviewing, testing." -- config panel visible, Role Resolver routes
- 6.0s-12.0s: "Each role has an ordered provider chain with fallbacks and circuit breakers. If Claude is down, it falls back to GPT-4. If that is slow, it routes to a local model." -- routing paths light up, security gate opens, dashboard materializes
- 12.0s-18.0s: "Diagnostics track cost, tokens, latency, and errors per provider..." -- dashboard metrics loading and updating
- 18.0s-24.0s: "...in real time." -- full dashboard view, data updating live, camera drifts across quadrants

#### EMOTIONAL ARC

- **Start**: Technical interest -- "How does the routing work?"
- **End**: Operational confidence -- "This gives full visibility and resilience"

#### TRANSITIONS

- **Enter**: Crossfade from Scene 11 (0.5s)
- **Exit**: Slide left to Scene 13 (0.4s) -- section transition to "The Differentiator"

#### SOUND

- Ambient: Soft config-typing sounds, routing beam whoosh, dashboard materialization tones, real-time data tick sounds, subtle warning blip on amber
- SFX Prompt (ElevenLabs): `{"text": "Soft keyboard clicks, electronic routing whoosh, dashboard data ticks, subtle amber warning blip, no music", "duration_seconds": 24.0}`

---


## SECTION D: THE DIFFERENTIATOR (Scenes 13-15, ~70s)

---

### Scene 13: Tamma Maintains Itself

**Duration**: 24 seconds | **Clips**: 3

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/13B-self-maintenance.png`
- **Description**: An ouroboros-like circular track viewed in 3D perspective. The Tamma logo (stylized bird/wing emblem) sits at the center, elevated and glowing purple. Along the circular track, event cards flow clockwise: a red bug card "#142: Fix retry logic" enters from the left, flows through "Author: Tamma" (with a commit code snippet), past "14/14 tests passed" in bold white, to "#143 Merged" (green checkmark) on the right. The track has holographic code panels and dashboard screens floating above it. Blue and purple energy traces along the ring. The composition shows Tamma finding its own bug, fixing it, testing it, and merging the fix -- a complete self-maintenance loop.

**Frame B (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/13C-self-maintenance.png`
- **Description**: A statistics view with split composition. Top left: "Human-Written: ~40%" in muted blue/gray text. Top right: "Self-Implemented: ~60%+" in bright green text (larger, more prominent). Center: a large progress bar reading "Self-Implementation: 60%+" filled mostly in green. Below: a horizontal timeline arrow from "Bootstrap" (left, with human+code icons) through three milestone dots -- "First Self-Fix" (purple dot), "First Self-Feature" (green dot), "First Self-Epic" (bright green dot) -- to "Now" (right, with Tamma icon). The timeline shows the progression from human bootstrapping to autonomous self-development. Dark navy background with subtle matrix/code rain.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame B
- **Duration**: 8 seconds
- **Motion Prompt**: "The circular self-maintenance track rotates slowly. The red bug card #142 slides along the track, passing through the commit stage. The Tamma author badge glows purple. Tests run and the '14/14 tests passed' counter increments rapidly. The green #143 Merged card appears with a satisfying glow. The camera rises above the track and the circular view begins flattening. The mood is one of awe -- the tool maintains itself."
- **Camera**: Rise above the ouroboros ring, beginning transition to statistical view
- **Output**: `clips/deep-dive/scene13-clip01.mp4`

**Clip 2**: Frame A --> Frame B (continued transition)
- **Duration**: 8 seconds
- **Motion Prompt**: "The circular view completes its transformation into the statistics layout. The progress bar fills from left to right, stopping at 60%+. The timeline materializes below with milestone dots appearing in sequence: Bootstrap, First Self-Fix, First Self-Feature, First Self-Epic. The 'Self-Implemented: ~60%+' text glows brighter than the 'Human-Written: ~40%' text. Code-rain particles begin drifting in the background. The mood shifts from technical demonstration to measurable achievement."
- **Camera**: Settling into overhead statistical view, elements materializing
- **Output**: `clips/deep-dive/scene13-clip02.mp4`

**Clip 3**: Hold on Frame B with subtle motion
- **Duration**: 8 seconds
- **Motion Prompt**: "The 60%+ progress bar pulses gently with green light. The timeline arrow extends slightly toward the right, suggesting ongoing progress. The milestone dots gleam in sequence: First Self-Fix, First Self-Feature, First Self-Epic. Subtle code-rain particles drift down in the background. The camera slowly pushes in on the progress bar and timeline, centering on the achievement. The mood is quietly impressive -- this IS the differentiator."
- **Camera**: Slow push-in on the progress bar and timeline
- **Output**: `clips/deep-dive/scene13-clip03.mp4`

#### NARRATION

**Text**:
> "Here is what separates Tamma from **every other tool** in this space. Tamma maintains **its own codebase**. It fixes its own bugs. It implements its own features. It updates its own dependencies. After the initial bootstrap, Tamma completed over **60 percent** of its own implementation **autonomously**. ... That is the ultimate proof of production readiness. If Tamma can safely maintain **mission-critical software** -- **itself** -- it can maintain **yours**."

**Delivery**: This is THE key scene. Start with authority: "Here is what separates Tamma." Build through the self-maintenance claims with increasing conviction. "60 percent" is delivered with pride. The final line -- "it can maintain yours" -- is the climax of the entire video. Spoken slowly, directly to the viewer.

**Voice Settings**:
- stability: 0.80
- similarity_boost: 0.85
- style: 0.48
- speed: 0.88

**Timing Sync**:
- 0.0s-6.0s: "Here is what separates Tamma from every other tool in this space. Tamma maintains its own codebase. It fixes its own bugs." -- ouroboros track, bug card flowing, tests passing
- 6.0s-12.0s: "It implements its own features. It updates its own dependencies. After the initial bootstrap, Tamma completed over 60 percent of its own implementation autonomously." -- merge completing, transition to stats, progress bar fills
- 12.0s-18.0s: "That is the ultimate proof of production readiness." -- progress bar settled, timeline milestones glowing
- 18.0s-24.0s: "If Tamma can safely maintain mission-critical software -- itself -- it can maintain yours." -- slow push-in on progress bar and timeline

#### EMOTIONAL ARC

- **Start**: Intrigue -- "What makes this different?"
- **End**: Conviction -- "If it can maintain itself, it can maintain my code too"

#### TRANSITIONS

- **Enter**: Slide left from Scene 12 (0.4s) -- section start
- **Exit**: Crossfade to Scene 14 (0.5s)

#### SOUND

- Ambient: Satisfying whoosh as bug card flows through the track, rapid test-tick sounds, triumphant merge tone, subtle awe-inspiring low drone on the 60% reveal
- SFX Prompt (ElevenLabs): `{"text": "Flowing whoosh, rapid ticking, triumphant merge chime, awe-inspiring deep drone, no music", "duration_seconds": 24.0}`

---

### Scene 14: Sarah's Story

**Duration**: 30 seconds | **Clips**: 3

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/14B-sarahs-story.png`
- **Description**: A chat-style interface showing Tamma's ambiguity detection. Top banner: "Codebase Scan: 3 existing auth patterns found". Center: Tamma's chat bubble (with Tamma icon) reads: "I found an ambiguity in issue #247. The endpoint could use refresh tokens or sessions. Which approach do you prefer?" Below, two large option cards: "Refresh Tokens" (key icon, highlighted with cursor pointing to it) and "Sessions" (cookie icon). Code panels visible in the background edges. Purple and cyan neon accents. The scene shows Tamma asking the one question that matters instead of guessing.

**Frame B (Mid Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/14C-sarahs-story.png`
- **Description**: A split-screen code view showing TDD in progress. Top left label: "Coding..." with "PR Draft #248" badge. Left panel: "oauth.test.ts" showing three test functions with green "Passed" checkmarks for the first two and amber "Running..." for the third. Tests include `should generate auth URL`, `should handle callback and exchange code`, `should refresh token`. Right panel: "oauth.controller.ts" showing the implementation code with `OAuthController` class, `handleCallback` and `refreshToken` methods. Center vertical bar shows "Implementation: 85%" progress. Brain icon at top. Green and cyan code highlighting. The scene shows test-first development happening live.

**Frame C (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/14D-sarahs-story.png`
- **Description**: Results summary. Top: a pipeline of small icons (code, database, test, security, review, deploy) all with green checkmarks. Center: "3 hours" (struck through in red) replaced by "45 minutes" (large, bold, white) with an arrow between them. A green "75 percent saved" badge sits to the right. Bottom left: Sarah's silhouette with speech bubble "I made 1 decision. Tamma handled the rest." Bottom right: a merged PR card "#248 Add OAuth2 Endpoint" with green checkmark, labeled "All quality gates passed" with Build, Tests, Security all checked. The mood is triumphant and practical.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame B
- **Duration**: 10 seconds
- **Motion Prompt**: "The chat interface shows Tamma's question about refresh tokens vs sessions. The cursor moves to the 'Refresh Tokens' card and clicks it -- the card lights up bright purple. A confirmation pulse radiates outward. The chat interface slides left and transforms into a code editor. Test files appear on the left, implementation code on the right. The first test runs and shows a green checkmark. Then the second. The implementation progress bar begins filling. The mood shifts from a simple question to rapid autonomous coding."
- **Camera**: Slide-left transition from chat to code editor, tests running in sequence
- **Output**: `clips/deep-dive/scene14-clip01.mp4`

**Clip 2**: Frame B --> Frame C
- **Duration**: 10 seconds
- **Motion Prompt**: "The third test turns green. The implementation progress bar hits 100%. The code editor folds away and a pipeline strip materializes at the top with icons lighting up green one by one: code, database, test, security, review, deploy. The '3 hours' text appears and gets struck through with a red line. '45 minutes' fades in large and bold with the '75% saved' badge. Sarah's silhouette appears with her speech bubble. The merged PR card slides in from the right. The mood is one of practical, measurable success."
- **Camera**: Fold transition from code to results summary, elements appearing in sequence
- **Output**: `clips/deep-dive/scene14-clip02.mp4`

**Clip 3**: Hold on Frame C with subtle motion
- **Duration**: 10 seconds
- **Motion Prompt**: "The results summary holds. The pipeline checkmarks pulse gently. The '45 minutes' text glows. Sarah's speech bubble is prominent: 'I made 1 decision. Tamma handled the rest.' The merged PR card's green checkmark gleams. The 75% badge rotates slightly. The camera slowly pushes in on the merged PR card and Sarah's quote, centering the human-in-the-loop message. Satisfied, practical, relatable atmosphere."
- **Camera**: Slow push-in centering on the PR card and Sarah's quote
- **Output**: `clips/deep-dive/scene14-clip03.mp4`

#### NARRATION

**Text**:
> "Let us walk through a **real scenario**. Sarah, a senior developer, assigns issue 247 -- add an OAuth2 authentication endpoint. Tamma analyzes the codebase, detects an **ambiguity** about refresh tokens versus sessions, and asks Sarah to choose. She picks refresh tokens. Tamma generates a design, she approves it, and Tamma implements the feature with TDD, creates the PR, passes all quality gates, and merges. Total time: **45 minutes**. Manually, it would have taken over **three hours**. Sarah focused on the **one decision that mattered**."

**Delivery**: Storytelling mode. Warm, relatable. Sarah feels like a real colleague. The ambiguity detection is the "aha" moment. "45 minutes" vs "three hours" is the practical payoff. "One decision that mattered" is the philosophical takeaway -- spoken with admiration.

**Voice Settings**:
- stability: 0.70
- similarity_boost: 0.80
- style: 0.50
- speed: 0.95

**Timing Sync**:
- 0.0s-8.0s: "Let us walk through a real scenario. Sarah, a senior developer, assigns issue 247 -- add an OAuth2 authentication endpoint. Tamma analyzes the codebase, detects an ambiguity about refresh tokens versus sessions, and asks Sarah to choose." -- chat interface with ambiguity question
- 8.0s-16.0s: "She picks refresh tokens. Tamma generates a design, she approves it, and Tamma implements the feature with TDD, creates the PR..." -- click, transition to code editor, tests running
- 16.0s-22.0s: "...passes all quality gates, and merges. Total time: 45 minutes. Manually, it would have taken over three hours." -- pipeline checkmarks, PR merging, time comparison
- 22.0s-30.0s: "Sarah focused on the one decision that mattered." -- results summary, Sarah's quote, slow push-in on merged PR

#### EMOTIONAL ARC

- **Start**: Narrative engagement -- "Tell me a story"
- **End**: Relatable aspiration -- "I want this for my team"

#### TRANSITIONS

- **Enter**: Crossfade from Scene 13 (0.5s)
- **Exit**: Crossfade to Scene 15 (0.5s)

#### SOUND

- Ambient: Soft chat notification on Tamma's question, click sound on choice, rapid coding/typing sounds during TDD, sequential checkmark pings on pipeline, satisfying merge tone
- SFX Prompt (ElevenLabs): `{"text": "Chat notification, button click, rapid typing, sequential success pings, satisfying merge chime, no music", "duration_seconds": 30.0}`

---

### Scene 15: AI That Learns

**Duration**: 16 seconds | **Clips**: 2

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/15B-ai-learns-feedback.png`
- **Description**: A three-stage learning pipeline flowing left to right. Stage 1 "Human Code Review": a developer avatar with a speech bubble "Use async/await instead of .then() chains." Stage 2 "Pattern Extraction": a central glowing brain on a circuit-board chip (the "Learning Engine"), with input arrows coming from the code review. Stage 3 "Knowledge Storage": file folder icons with a card reading "Pattern: Prefer async/await. Source: Review #48. Confidence: High." A counter badge in the lower right: "328 patterns learned". Dark navy background with circuit traces and vertical metric bars on the right edge. The pipeline shows how human feedback becomes encoded knowledge.

**Frame B (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/15C-ai-learns-feedback.png`
- **Description**: Three improvement metric panels arranged in a row. Left panel "REVIEW COMMENTS": shows Cycle 1 at 12 comments dropping to Cycle 20 at 2 comments, with a downward arrow. Label: "Total Comments Reduced". Center panel "COMPLETION TIME": shows "Then: 3.5 Hours" dropping to "Now: 45 Min" with a downward arrow. Label: "Faster Delivery". Right panel "FIRST-PASS APPROVAL": shows "Then: 35% (pending)" rising to "Now: 87% (verified)" with an upward green arrow. Label: "Quality Increased". Below all three panels: "CONTINUOUS IMPROVEMENT" text with glowing circular orbital lines. The metrics prove the learning system works over time.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame B
- **Duration**: 8 seconds
- **Motion Prompt**: "The human code review speech bubble sends its feedback toward the Learning Engine brain chip. The brain processes the input with a neural network pulse effect -- synapses firing in blue and purple. A knowledge card materializes and files itself into the Knowledge Storage folder. The pattern counter increments. The camera pulls back and the learning pipeline compresses, transforming into three metric panels that expand to fill the frame. Numbers begin counting: review comments drop, completion time shrinks, approval rate climbs. The mood transitions from technical process to measurable results."
- **Camera**: Pull-back from pipeline, panels expanding outward
- **Output**: `clips/deep-dive/scene15-clip01.mp4`

**Clip 2**: Hold on Frame B with subtle motion
- **Duration**: 8 seconds
- **Motion Prompt**: "The three metric panels are alive with subtle animation. The review comment counter flickers between 2 and 3. The completion time display pulses at 45 min. The approval rate gauge fills to 87% with a satisfying green glow. The 'CONTINUOUS IMPROVEMENT' text glows, and the orbital lines beneath rotate slowly, suggesting perpetual learning. The camera slowly drifts inward, centering on the improvement metrics. The mood is one of proven, measurable growth."
- **Camera**: Slow drift-in, centering on the metric panels
- **Output**: `clips/deep-dive/scene15-clip02.mp4`

#### NARRATION

**Text**:
> "Tamma does not just execute -- it **learns**. The mentorship workflow captures **every piece of human feedback**. Code review comments. Design decisions. Quality preferences. Over time, Tamma **adapts** to your team's coding style, architectural patterns, and quality standards. It gets **better every cycle**."

**Delivery**: Warm and forward-looking. "It learns" is a gentle reveal. The examples are conversational. "Adapts to your team" makes it personal. "Better every cycle" is the optimistic closer -- spoken with genuine enthusiasm.

**Voice Settings**:
- stability: 0.72
- similarity_boost: 0.80
- style: 0.45
- speed: 0.93

**Timing Sync**:
- 0.0s-4.0s: "Tamma does not just execute -- it learns. The mentorship workflow captures every piece of human feedback." -- code review flows into learning engine
- 4.0s-8.0s: "Code review comments. Design decisions. Quality preferences." -- knowledge cards filing, counter incrementing
- 8.0s-12.0s: "Over time, Tamma adapts to your team's coding style, architectural patterns, and quality standards." -- metric panels appearing, numbers counting
- 12.0s-16.0s: "It gets better every cycle." -- all three metrics settled at impressive values, orbital lines rotating

#### EMOTIONAL ARC

- **Start**: Pleasant surprise -- "It actually learns from feedback?"
- **End**: Long-term optimism -- "This gets better over time"

#### TRANSITIONS

- **Enter**: Crossfade from Scene 14 (0.5s)
- **Exit**: Purple glow wipe to Scene 16 (0.7s) -- section transition to "Architecture & Vision"

#### SOUND

- Ambient: Neural network pulse/synapse firing sounds, knowledge filing whoosh, metric counter ticking, subtle ascending tone on "better every cycle"
- SFX Prompt (ElevenLabs): `{"text": "Neural synapse pulses, filing whoosh, soft counter ticking, ascending optimistic tone, no music", "duration_seconds": 16.0}`

---


## SECTION E: ARCHITECTURE AND VISION (Scenes 16-18, ~68s)

---

### Scene 16: Built for Production

**Duration**: 24 seconds | **Clips**: 3

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/16B-built-for-production.png`
- **Description**: A vertical architecture stack diagram framed as a production control panel. Top layer: "Fastify 5 API" with a lightning bolt icon and "100K req/s" badge, labeled "PRODUCTION READY" and "LIGHTNING FAST". Right side labels: "LOAD BALANCER", "FRONTEND / UI". Middle layer: "ELSA/.NET Workflows" with workflow diagram icon and "20+ workflows" badge, labeled "SCALABLE ORCHESTRATION". Shows EVENT WRITE and EVENT READ operations flowing to the bottom. Bottom layer: "PostgreSQL 17" with database icon and "JSONB events" label, labeled "ADVANCED DATA HANDLING". Shows a SQL query "SELECT * FROM events WHERE type = 'type'". The layers are connected by data flow arrows. Dark navy background with amber/gold and green accents. Technical, production-grade feel.

**Frame B (Mid Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/16C-built-for-production.png`
- **Description**: A 12-package monorepo grid. Each package is a rounded square card with a unique icon and color: cli (terminal icon, gray), orchestrator (refresh icon, orange), workers (gear icon, teal), gates (shield icon, red), intelligence (brain icon, purple), events (circuit icon, green), providers (plug icon, yellow), platforms (connection icon, pink), api (lightning icon, blue), dashboard (chart icon, orange), observability (eye icon, red), shared (chain icon, bright purple/blue). All cards are interconnected with blue and purple lines showing dependencies. An "Open Source" badge with heart icon sits in the top right corner. Dark navy background with circuit traces.

**Frame C (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/16D-built-for-production.png`
- **Description**: A production deployment visualization. Left: a "Hetzner VPS" server rack with "Host Server" label. Center: a Docker whale carrying a stack of colored containers: PostgreSQL (blue), RabbitMQ (orange), ELSA Engine (purple), API Server (teal), Dashboard (blue), Nginx (gray) -- stacked like shipping containers. Top label: "Production Live" with heartbeat icon. Right: a Cloudflare shield icon with arrows showing "api.tamma.dev" and "app.tamma.dev" domains routing through it. The composition shows the full deployment chain from hardware to DNS. Dark navy background.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame B
- **Duration**: 8 seconds
- **Motion Prompt**: "The Fastify API layer at the top pulses with a lightning bolt flash. The '100K req/s' badge glows. Data flows downward through ELSA workflows to PostgreSQL. The camera pulls back and the vertical stack compresses, transforming into a grid layout. Twelve package cards materialize one by one in a 4x3 grid, each with its distinct color and icon. Connection lines draw between them, showing the monorepo dependency graph. The 'Open Source' badge fades in at the corner. The mood is one of engineering thoroughness."
- **Camera**: Pull-back from vertical stack, compressing into grid layout
- **Output**: `clips/deep-dive/scene16-clip01.mp4`

**Clip 2**: Frame B --> Frame C
- **Duration**: 8 seconds
- **Motion Prompt**: "The 12 package cards compact and stack on top of each other, transforming into Docker containers on the Docker whale's back. The Hetzner server rack materializes on the left. The container stack builds from bottom (PostgreSQL) to top (Nginx). The 'Production Live' label appears with a heartbeat pulse. The Cloudflare shield materializes on the right with domain arrows routing through it. The camera settles into a wide establishing shot showing the complete deployment architecture. The mood is one of it being real and running."
- **Camera**: Packages compacting into containers, widening to show deployment chain
- **Output**: `clips/deep-dive/scene16-clip02.mp4`

**Clip 3**: Hold on Frame C with subtle motion
- **Duration**: 8 seconds
- **Motion Prompt**: "The production deployment hums. The heartbeat on 'Production Live' pulses steadily. Container layers glow softly in sequence. The Cloudflare shield rotates slightly. Domain name arrows flow with data packets. The Docker whale sways gently. The server rack LEDs blink. The camera slowly pushes in toward the 'Production Live' label, emphasizing that this is real and running. Confident, operational atmosphere."
- **Camera**: Slow push-in toward the center, production live pulse
- **Output**: `clips/deep-dive/scene16-clip03.mp4`

#### NARRATION

**Text**:
> "Tamma is built on a **production-grade stack**. TypeScript 5.7 with strict mode. Node.js 22 LTS. PostgreSQL 17 for the event store. Fastify 5 for the API. ELSA Workflows on dotnet for orchestration. Vitest for testing -- 10 to 20x faster than Jest. Pino for logging -- 5x faster than Winston. **pnpm monorepo with 12 packages.** Docker Compose for deployment. Everything is **open source** under an open-core model."

**Delivery**: Rapid-fire technical authority. Each technology name is crisp. The comparison benchmarks ("10 to 20x faster than Jest") add weight. "12 packages" shows scale. "Open source" at the end is the community hook. Confident, almost proud.

**Voice Settings**:
- stability: 0.70
- similarity_boost: 0.78
- style: 0.42
- speed: 1.00

**Timing Sync**:
- 0.0s-6.0s: "Tamma is built on a production-grade stack. TypeScript 5.7 with strict mode. Node.js 22 LTS. PostgreSQL 17 for the event store. Fastify 5 for the API." -- architecture stack layers lighting up
- 6.0s-12.0s: "ELSA Workflows on dotnet for orchestration. Vitest for testing -- 10 to 20x faster than Jest. Pino for logging -- 5x faster than Winston." -- grid of 12 packages appearing
- 12.0s-18.0s: "pnpm monorepo with 12 packages. Docker Compose for deployment." -- packages stacking into containers on Docker whale
- 18.0s-24.0s: "Everything is open source under an open-core model." -- full deployment chain, Production Live pulse

#### EMOTIONAL ARC

- **Start**: Technical respect -- "Serious technology choices"
- **End**: Operational trust -- "This is real, running production software"

#### TRANSITIONS

- **Enter**: Purple glow wipe from Scene 15 (0.7s) -- section start
- **Exit**: Crossfade to Scene 17 (0.5s)

#### SOUND

- Ambient: Lightning bolt electrical crack on Fastify, soft materialization sounds for each package, Docker container stacking thuds (subtle), heartbeat monitor beep on Production Live
- SFX Prompt (ElevenLabs): `{"text": "Electrical crack, soft materialization tones, subtle container stacking, heartbeat beep, no music", "duration_seconds": 24.0}`

---

### Scene 17: Where We Are

**Duration**: 24 seconds | **Clips**: 3

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/17B-where-we-are.png`
- **Description**: A 5x5 grid of epic tiles viewed in 3D perspective. The top three rows (16 tiles) are bright green with checkmark icons, each labeled with an epic name: AI Providers, Git Platforms, CLI, Events, Orchestrator, Quality Gates, Security, Auth, API, Dashboard, Monitoring, Workflows, Workers, Config, Plugins, Observability. The bottom two rows (8 tiles) are dimmed purple/outlined, representing upcoming work: Autonomous Loop, Self-Maintenance, Billing, Multi-Tenancy, SaaS Platform, Enterprise, Marketplace, Advanced AI. The green section glows brightly while the purple section is muted, showing clear progress. Dark navy background with circuit traces.

**Frame B (Mid Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/17C-where-we-are.png`
- **Description**: A live production dashboard titled "Tamma Dashboard -- Production" with an "Online" green badge. Four metric panels: (1) "Uptime: 99.8%" with a line graph trending upward. (2) "Workflows Executed: 1,247" with a bar chart showing daily execution counts (Mon-Sun). (3) "Issues Processed: 65" with a funnel visualization: New Issues (80) > Investigating (15) > In Progress (30) > Resolved (65). (4) "Active Providers: 4/8" with eight provider circles, four green (active) and four gray. Bottom right: CPU 32%, MEM 45% gauges. Tamma logo in corner. Cyberpunk city skyline through window behind dashboard.

**Frame C (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/17D-where-we-are.png`
- **Description**: A roadmap timeline with momentum arrow. Left side: completed milestones with green checkmarks -- Foundation, Core Platform, Quality & Security, Auth & API. Center: "Current" point labeled "Workflows & Monitoring" with a glowing purple portal effect. Right side: upcoming milestones with golden/amber glow -- Autonomous Loop, SaaS Platform, Enterprise. Above the timeline, a "Momentum" label with a large upward arrow sweeping toward the city skyline. Below: three stat badges: "16 epics shipped" (green), "220+ stories documented" (purple), "65 issues closed" (blue). The arrow conveys acceleration and inevitability.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame B
- **Duration**: 8 seconds
- **Motion Prompt**: "The 5x5 epic grid rotates slightly. The 16 green tiles pulse with checkmark confirmations. The camera pushes forward and the grid transforms into a live dashboard. Metric panels materialize: uptime graph draws upward, workflow bar chart fills, the issues funnel animates from top to bottom, provider circles light up one by one. The 'Online' badge blinks. The dashboard feels alive with real production data. The mood shifts from progress report to operational reality."
- **Camera**: Push-in through the grid, transforming into dashboard
- **Output**: `clips/deep-dive/scene17-clip01.mp4`

**Clip 2**: Frame B --> Frame C
- **Duration**: 8 seconds
- **Motion Prompt**: "The dashboard panels compress and slide upward, revealing a horizontal timeline below. Completed milestones appear from left to right with green checkmarks. The 'Current' portal pulses at the center. Upcoming milestones materialize with golden glow to the right. The 'Momentum' arrow sweeps upward with energy particles trailing behind it. The stat badges (16 epics, 220+ stories, 65 issues) fade in at the bottom. The camera settles into a wide view showing both the past achievements and future direction."
- **Camera**: Upward slide transition, then wide establishing shot of timeline
- **Output**: `clips/deep-dive/scene17-clip02.mp4`

**Clip 3**: Hold on Frame C with subtle motion
- **Duration**: 8 seconds
- **Motion Prompt**: "The momentum arrow gleams and pulses with energy. The city skyline shimmers behind it. Completed milestone checkmarks glow green in sequence. The 'Current' portal rotates slowly. Upcoming milestones cast a warm golden glow. The stat badges pulse: 16, 220+, 65. The camera slowly pushes toward the momentum arrow and the future milestones, conveying forward trajectory. Optimistic, energetic atmosphere."
- **Camera**: Slow push-in toward the future milestones and momentum arrow
- **Output**: `clips/deep-dive/scene17-clip03.mp4`

#### NARRATION

**Text**:
> "Tamma is in **active development** with **16 epics** completed and deployed. The foundation is live: AI providers, Git platforms, CLI, API, workflows, quality gates, security, authentication, and monitoring. All running on a production server with Docker Compose, Cloudflare DNS, and full SSL. The remaining **8 epics** cover the autonomous development loop, billing, multi-tenancy, and the SaaS platform. **65 GitHub issues closed**. 24 epics total. **220-plus stories** documented."

**Delivery**: Energetic progress report. The completed items are listed with momentum. "16 epics" is impressive. The production details add credibility. The remaining work shows ambition without overcommitting. The stats at the end are punchy: 65, 24, 220+.

**Voice Settings**:
- stability: 0.70
- similarity_boost: 0.78
- style: 0.45
- speed: 0.98

**Timing Sync**:
- 0.0s-6.0s: "Tamma is in active development with 16 epics completed and deployed. The foundation is live: AI providers, Git platforms, CLI, API, workflows, quality gates, security, authentication, and monitoring." -- green epic grid, checkmarks lighting up, dashboard emerging
- 6.0s-12.0s: "All running on a production server with Docker Compose, Cloudflare DNS, and full SSL." -- dashboard showing production metrics
- 12.0s-18.0s: "The remaining 8 epics cover the autonomous development loop, billing, multi-tenancy, and the SaaS platform." -- transition to roadmap timeline, upcoming milestones
- 18.0s-24.0s: "65 GitHub issues closed. 24 epics total. 220-plus stories documented." -- stat badges appearing, momentum arrow

#### EMOTIONAL ARC

- **Start**: Impressed by progress -- "They've already done a LOT"
- **End**: Forward momentum -- "This is going somewhere real"

#### TRANSITIONS

- **Enter**: Crossfade from Scene 16 (0.5s)
- **Exit**: Crossfade to Scene 18 (0.5s)

#### SOUND

- Ambient: Epic grid checkmark pings (quick sequence), dashboard data materialization tones, timeline whoosh as milestones appear, momentum arrow ascending tone
- SFX Prompt (ElevenLabs): `{"text": "Quick checkmark pings, data materialization, timeline sweep, ascending momentum tone, no music", "duration_seconds": 24.0}`

---

### Scene 18: Join the Movement

**Duration**: 20 seconds | **Clips**: 2

#### IMAGES

**Frame A (Start Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/18B-join-the-movement.png`
- **Description**: A community-focused scene. Top: "Tamma" logo text with "Open Source" label and heart icon. Center: a GitHub-style contribution graph (green squares grid) forming a thumbs-up shape, showing active development. Right side: a "Stars" badge with golden stars and an upward-trending arrow, encouraging GitHub stars. Bottom: a row of eight circular developer avatar silhouettes connected by lines to the Tamma project, representing the community. A "+" icon at the end invites more contributors. Cyberpunk room setting with holographic screens. Purple and green tones.

**Frame B (Mid Frame)**
- **Path**: `docs/video/scenes/deep-dive/extra/18C-join-the-movement.png`
- **Description**: A community gathering scene. Center: the Tamma "T" logo in a golden circular badge, large and prominent. Surrounding it, eight developer figures in hooded silhouettes connected by flowing purple and green neon lines, forming a network. Text below: "Join the Movement" in clean bold white. Bottom: three CTA icons -- GitHub (gold octocat), Discord (purple icon), Documentation (blue book). The mood is warm, inclusive, inviting. Deep purple gradient background from navy to purple. The composition emphasizes community around a shared mission.

**Frame C (End Frame)**
- **Path**: `docs/video/scenes/deep-dive/18-cta.png`
- **Description**: Grand closing shot. Center: the Tamma Arabic calligraphy logo -- ornate golden swirling "tamm" text inside a glowing purple circular ring. Light rays emanate outward from the logo. Above: "Autonomous Development Done Right" tagline in subtle white. Below: "tamma.dev" in large clean white text. Beneath that: a GitHub octocat icon (small, gray). The background is a rich deep purple gradient with scattered stars/sparkle particles and circuit-board traces fading into the edges. Premium, luminous, definitive. This is the final frame the viewer sees.

#### VIDEO CLIPS

**Clip 1**: Frame A --> Frame B
- **Duration**: 10 seconds
- **Motion Prompt**: "The GitHub contribution graph fills with green squares, building the thumbs-up shape. Stars accumulate on the star counter. The developer avatars pulse in sequence. The camera pushes forward through the open-source scene, and the contribution graph compresses into the Tamma logo. Developer silhouettes expand outward into the surrounding network of hooded figures. 'Join the Movement' text fades in below. The three CTA icons materialize. The mood shifts from project metrics to community invitation."
- **Camera**: Push-in through contribution graph, expanding into community network
- **Output**: `clips/deep-dive/scene18-clip01.mp4`

**Clip 2**: Frame B --> Frame C
- **Duration**: 10 seconds
- **Motion Prompt**: "The community network of developer figures contracts inward toward the center Tamma logo. As they merge with it, the logo transforms from the simple 'T' badge into the ornate golden Arabic calligraphy. Light rays burst outward from the calligraphy as it fully materializes. The purple ring around the logo glows intensely then settles into a steady warm glow. 'Autonomous Development Done Right' fades in above. 'tamma.dev' fades in below in large clean text. The GitHub icon appears beneath. Sparkle particles drift across the frame. The camera slowly zooms out to reveal the full grand composition. The mood is triumphant, warm, and final. Hold for 3 seconds on the completed composition before fade to black."
- **Camera**: Inward contraction, then slow zoom-out revealing grand finale, hold
- **Output**: `clips/deep-dive/scene18-clip02.mp4`

#### NARRATION

**Text**:
> "Tamma is **open source** and in active development. We are building the future of **autonomous development** -- transparent, multi-provider, self-maintaining, and **yours to control**. Star us on GitHub. Sign up for launch notifications at **tamma.dev**. Or dive into the code and contribute. ... The Arabic word '**tamm**' means '**it is done**.' With Tamma ... your development work will be too."

**Delivery**: Warm, inviting, slightly elevated. This is the final call to action. "Open source" is spoken with pride. The four adjectives (transparent, multi-provider, self-maintaining, yours to control) are a deliberate callback to the whole video. "Tamma.dev" is clear and memorable. The final line -- "your development work will be too" -- is spoken slowly, with a smile in the voice. It is the last thing the viewer hears.

**Voice Settings**:
- stability: 0.78
- similarity_boost: 0.85
- style: 0.50
- speed: 0.88

**Timing Sync**:
- 0.0s-5.0s: "Tamma is open source and in active development. We are building the future of autonomous development -- transparent, multi-provider, self-maintaining, and yours to control." -- community scene, contribution graph
- 5.0s-10.0s: "Star us on GitHub. Sign up for launch notifications at tamma.dev. Or dive into the code and contribute." -- CTA icons appear, community gathers
- 10.0s-15.0s: "The Arabic word 'tamm' means 'it is done.'" -- network merges into golden calligraphy, light rays burst
- 15.0s-20.0s: "With Tamma, your development work will be too." -- grand finale composition, hold, begin fade

#### EMOTIONAL ARC

- **Start**: Community warmth -- "I want to be part of this"
- **End**: Inspired conviction -- "Tamm. It is done. I'm in."

#### TRANSITIONS

- **Enter**: Crossfade from Scene 17 (0.5s)
- **Exit**: Fade to black (1.5s) -- long, luxurious fade signaling the end

#### SOUND

- Ambient: Warm community hum, subtle star twinkle sounds, golden chime on calligraphy reveal, deep resonant "completion" tone on the final word, gentle fade to silence
- SFX Prompt (ElevenLabs): `{"text": "Warm community ambiance, star twinkles, golden crystalline chime, deep resonant completion tone, fading to silence, no music", "duration_seconds": 20.0}`

---


## Stitching & Transitions (ffmpeg)

### Clip Inventory (43 clips via Runway Gen4 Turbo)

| Scene | Clips | Filenames | Runway Duration |
|-------|-------|-----------|-----------------|
| 1 | 2 | scene01-clip01.mp4, scene01-clip02.mp4 | 8s + 8s |
| 2 | 3 | scene02-clip01.mp4, scene02-clip02.mp4, scene02-clip03.mp4 | 8s + 8s + 8s |
| 3 | 2 | scene03-clip01.mp4, scene03-clip02.mp4 | 8s + 8s |
| 4 | 2 | scene04-clip01.mp4, scene04-clip02.mp4 | 8s + 8s |
| 5 | 2 | scene05-clip01.mp4, scene05-clip02.mp4 | 10s + 8s |
| 6 | 2 | scene06-clip01.mp4, scene06-clip02.mp4 | 8s + 8s |
| 7 | 2 | scene07-clip01.mp4, scene07-clip02.mp4 | 10s + 10s |
| 8 | 2 | scene08-clip01.mp4, scene08-clip02.mp4 | 8s + 8s |
| 9 | 2 | scene09-clip01.mp4, scene09-clip02.mp4 | 10s + 8s |
| 10 | 3 | scene10-clip01.mp4, scene10-clip02.mp4, scene10-clip03.mp4 | 8s + 8s + 8s |
| 11 | 2 | scene11-clip01.mp4, scene11-clip02.mp4 | 10s + 10s |
| 12 | 3 | scene12-clip01.mp4, scene12-clip02.mp4, scene12-clip03.mp4 | 8s + 8s + 8s |
| 13 | 3 | scene13-clip01.mp4, scene13-clip02.mp4, scene13-clip03.mp4 | 8s + 8s + 8s |
| 14 | 3 | scene14-clip01.mp4, scene14-clip02.mp4, scene14-clip03.mp4 | 10s + 10s + 10s |
| 15 | 2 | scene15-clip01.mp4, scene15-clip02.mp4 | 8s + 8s |
| 16 | 3 | scene16-clip01.mp4, scene16-clip02.mp4, scene16-clip03.mp4 | 8s + 8s + 8s |
| 17 | 3 | scene17-clip01.mp4, scene17-clip02.mp4, scene17-clip03.mp4 | 8s + 8s + 8s |
| 18 | 2 | scene18-clip01.mp4, scene18-clip02.mp4 | 10s + 10s |

All clip durations use Runway Gen 4.5 supported values (5s, 8s, or 10s). No trimming required.

### Transition Map

| From | To | Type | Duration |
|------|----|------|----------|
| Black | Scene 1 | Fade from black | 0.8s |
| Scene 1 | Scene 2 | Crossfade | 0.5s |
| Scene 2 | Scene 3 | Crossfade | 0.5s |
| Scene 3 | Scene 4 | Purple glow wipe | 0.7s |
| Scene 4 | Scene 5 | Crossfade | 0.5s |
| Scene 5 | Scene 6 | Crossfade | 0.5s |
| Scene 6 | Scene 7 | Slide left | 0.4s |
| Scene 7 | Scene 8 | Crossfade | 0.5s |
| Scene 8 | Scene 9 | Crossfade | 0.5s |
| Scene 9 | Scene 10 | Crossfade | 0.5s |
| Scene 10 | Scene 11 | Crossfade | 0.5s |
| Scene 11 | Scene 12 | Crossfade | 0.5s |
| Scene 12 | Scene 13 | Slide left | 0.4s |
| Scene 13 | Scene 14 | Crossfade | 0.5s |
| Scene 14 | Scene 15 | Crossfade | 0.5s |
| Scene 15 | Scene 16 | Purple glow wipe | 0.7s |
| Scene 16 | Scene 17 | Crossfade | 0.5s |
| Scene 17 | Scene 18 | Crossfade | 0.5s |
| Scene 18 | Black | Fade to black | 1.5s |

### Step 0: Upscale Runway clips from 720p to 1080p

Runway Gen4 Turbo outputs at 1280x720. Upscale all clips before stitching.

```bash
CLIPS=docs/video/clips/deep-dive

for f in $CLIPS/scene*.mp4; do
  base=$(basename "$f" .mp4)
  ffmpeg -i "$f" -vf "scale=1920:1080:flags=lanczos" \
    -c:v libx264 -preset slow -crf 18 \
    "$CLIPS/${base}-1080p.mp4"
done
```

### Step 1: Concatenate clips within each scene (no transition between intra-scene clips)

```bash
CLIPS=docs/video/clips/deep-dive
OUTPUT=docs/video/output

# Scene 1 (2 clips)
ffmpeg -i $CLIPS/scene01-clip01.mp4 -i $CLIPS/scene01-clip02.mp4 \
  -filter_complex "[0:v][1:v]concat=n=2:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene01-merged.mp4

# Scene 2 (3 clips)
ffmpeg -i $CLIPS/scene02-clip01.mp4 -i $CLIPS/scene02-clip02.mp4 -i $CLIPS/scene02-clip03.mp4 \
  -filter_complex "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene02-merged.mp4

# Scene 3 (2 clips)
ffmpeg -i $CLIPS/scene03-clip01.mp4 -i $CLIPS/scene03-clip02.mp4 \
  -filter_complex "[0:v][1:v]concat=n=2:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene03-merged.mp4

# Scene 4 (2 clips)
ffmpeg -i $CLIPS/scene04-clip01.mp4 -i $CLIPS/scene04-clip02.mp4 \
  -filter_complex "[0:v][1:v]concat=n=2:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene04-merged.mp4

# Scene 5 (2 clips)
ffmpeg -i $CLIPS/scene05-clip01.mp4 -i $CLIPS/scene05-clip02.mp4 \
  -filter_complex "[0:v][1:v]concat=n=2:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene05-merged.mp4

# Scene 6 (2 clips)
ffmpeg -i $CLIPS/scene06-clip01.mp4 -i $CLIPS/scene06-clip02.mp4 \
  -filter_complex "[0:v][1:v]concat=n=2:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene06-merged.mp4

# Scene 7 (2 clips)
ffmpeg -i $CLIPS/scene07-clip01.mp4 -i $CLIPS/scene07-clip02.mp4 \
  -filter_complex "[0:v][1:v]concat=n=2:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene07-merged.mp4

# Scene 8 (2 clips)
ffmpeg -i $CLIPS/scene08-clip01.mp4 -i $CLIPS/scene08-clip02.mp4 \
  -filter_complex "[0:v][1:v]concat=n=2:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene08-merged.mp4

# Scene 9 (2 clips)
ffmpeg -i $CLIPS/scene09-clip01.mp4 -i $CLIPS/scene09-clip02.mp4 \
  -filter_complex "[0:v][1:v]concat=n=2:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene09-merged.mp4

# Scene 10 (3 clips)
ffmpeg -i $CLIPS/scene10-clip01.mp4 -i $CLIPS/scene10-clip02.mp4 -i $CLIPS/scene10-clip03.mp4 \
  -filter_complex "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene10-merged.mp4

# Scene 11 (2 clips)
ffmpeg -i $CLIPS/scene11-clip01.mp4 -i $CLIPS/scene11-clip02.mp4 \
  -filter_complex "[0:v][1:v]concat=n=2:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene11-merged.mp4

# Scene 12 (3 clips)
ffmpeg -i $CLIPS/scene12-clip01.mp4 -i $CLIPS/scene12-clip02.mp4 -i $CLIPS/scene12-clip03.mp4 \
  -filter_complex "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene12-merged.mp4

# Scene 13 (3 clips)
ffmpeg -i $CLIPS/scene13-clip01.mp4 -i $CLIPS/scene13-clip02.mp4 -i $CLIPS/scene13-clip03.mp4 \
  -filter_complex "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene13-merged.mp4

# Scene 14 (3 clips)
ffmpeg -i $CLIPS/scene14-clip01.mp4 -i $CLIPS/scene14-clip02.mp4 -i $CLIPS/scene14-clip03.mp4 \
  -filter_complex "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene14-merged.mp4

# Scene 15 (2 clips)
ffmpeg -i $CLIPS/scene15-clip01.mp4 -i $CLIPS/scene15-clip02.mp4 \
  -filter_complex "[0:v][1:v]concat=n=2:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene15-merged.mp4

# Scene 16 (3 clips)
ffmpeg -i $CLIPS/scene16-clip01.mp4 -i $CLIPS/scene16-clip02.mp4 -i $CLIPS/scene16-clip03.mp4 \
  -filter_complex "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene16-merged.mp4

# Scene 17 (3 clips)
ffmpeg -i $CLIPS/scene17-clip01.mp4 -i $CLIPS/scene17-clip02.mp4 -i $CLIPS/scene17-clip03.mp4 \
  -filter_complex "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene17-merged.mp4

# Scene 18 (2 clips)
ffmpeg -i $CLIPS/scene18-clip01.mp4 -i $CLIPS/scene18-clip02.mp4 \
  -filter_complex "[0:v][1:v]concat=n=2:v=1:a=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/scene18-merged.mp4
```

### Step 2: Apply transitions between scenes using xfade

Note: `xfade` filter requires knowing exact durations. Replace `DURATION_N` with actual scene duration in seconds. The `offset` for each xfade is the cumulative runtime minus the transition overlap.

```bash
OUTPUT=docs/video/output

# Build the full xfade chain
# Scene durations: 16, 24, 16, 16, 18, 16, 20, 16, 18, 24, 20, 24, 24, 30, 16, 24, 24, 20
# Transition durations: 0.8(in), 0.5, 0.5, 0.7, 0.5, 0.5, 0.4, 0.5, 0.5, 0.5, 0.5, 0.5, 0.4, 0.5, 0.5, 0.7, 0.5, 0.5, 1.5(out)

# Pairwise transitions (run sequentially, each takes previous output):

# Fade in from black for Scene 1
ffmpeg -f lavfi -i "color=c=black:s=1920x1080:d=0.8" -i $OUTPUT/scene01-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=fade:duration=0.8:offset=0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-01.mp4

# Scene 1 -> Scene 2 (crossfade 0.5s)
ffmpeg -i $OUTPUT/step-01.mp4 -i $OUTPUT/scene02-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=fade:duration=0.5:offset=15.5[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-02.mp4

# Scene 2 -> Scene 3 (crossfade 0.5s)
ffmpeg -i $OUTPUT/step-02.mp4 -i $OUTPUT/scene03-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=fade:duration=0.5:offset=39.0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-03.mp4

# Scene 3 -> Scene 4 (wipeleft 0.7s -- purple wipe approximated)
ffmpeg -i $OUTPUT/step-03.mp4 -i $OUTPUT/scene04-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=wipeleft:duration=0.7:offset=54.3[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-04.mp4

# Scene 4 -> Scene 5 (crossfade 0.5s)
ffmpeg -i $OUTPUT/step-04.mp4 -i $OUTPUT/scene05-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=fade:duration=0.5:offset=69.8[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-05.mp4

# Scene 5 -> Scene 6 (crossfade 0.5s)
ffmpeg -i $OUTPUT/step-05.mp4 -i $OUTPUT/scene06-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=fade:duration=0.5:offset=87.3[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-06.mp4

# Scene 6 -> Scene 7 (slideleft 0.4s)
ffmpeg -i $OUTPUT/step-06.mp4 -i $OUTPUT/scene07-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=slideleft:duration=0.4:offset=102.9[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-07.mp4

# Scene 7 -> Scene 8 (crossfade 0.5s)
ffmpeg -i $OUTPUT/step-07.mp4 -i $OUTPUT/scene08-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=fade:duration=0.5:offset=122.4[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-08.mp4

# Scene 8 -> Scene 9 (crossfade 0.5s)
ffmpeg -i $OUTPUT/step-08.mp4 -i $OUTPUT/scene09-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=fade:duration=0.5:offset=137.9[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-09.mp4

# Scene 9 -> Scene 10 (crossfade 0.5s)
ffmpeg -i $OUTPUT/step-09.mp4 -i $OUTPUT/scene10-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=fade:duration=0.5:offset=155.4[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-10.mp4

# Scene 10 -> Scene 11 (crossfade 0.5s)
ffmpeg -i $OUTPUT/step-10.mp4 -i $OUTPUT/scene11-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=fade:duration=0.5:offset=178.9[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-11.mp4

# Scene 11 -> Scene 12 (crossfade 0.5s)
ffmpeg -i $OUTPUT/step-11.mp4 -i $OUTPUT/scene12-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=fade:duration=0.5:offset=198.4[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-12.mp4

# Scene 12 -> Scene 13 (slideleft 0.4s)
ffmpeg -i $OUTPUT/step-12.mp4 -i $OUTPUT/scene13-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=slideleft:duration=0.4:offset=222.0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-13.mp4

# Scene 13 -> Scene 14 (crossfade 0.5s)
ffmpeg -i $OUTPUT/step-13.mp4 -i $OUTPUT/scene14-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=fade:duration=0.5:offset=245.5[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-14.mp4

# Scene 14 -> Scene 15 (crossfade 0.5s)
ffmpeg -i $OUTPUT/step-14.mp4 -i $OUTPUT/scene15-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=fade:duration=0.5:offset=275.0[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-15.mp4

# Scene 15 -> Scene 16 (wipeleft 0.7s -- purple glow wipe)
ffmpeg -i $OUTPUT/step-15.mp4 -i $OUTPUT/scene16-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=wipeleft:duration=0.7:offset=290.3[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-16.mp4

# Scene 16 -> Scene 17 (crossfade 0.5s)
ffmpeg -i $OUTPUT/step-16.mp4 -i $OUTPUT/scene17-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=fade:duration=0.5:offset=313.8[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-17.mp4

# Scene 17 -> Scene 18 (crossfade 0.5s)
ffmpeg -i $OUTPUT/step-17.mp4 -i $OUTPUT/scene18-merged.mp4 \
  -filter_complex "[0:v][1:v]xfade=transition=fade:duration=0.5:offset=337.3[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/step-18.mp4

# Fade to black at the end (1.5s)
ffmpeg -i $OUTPUT/step-18.mp4 -f lavfi -i "color=c=black:s=1920x1080:d=1.5" \
  -filter_complex "[0:v][1:v]xfade=transition=fade:duration=1.5:offset=355.8[v]" \
  -map "[v]" -c:v libx264 -preset slow -crf 18 $OUTPUT/deep-dive-video-only.mp4
```

**Note**: The offset values above are estimates based on scene durations. Actual offsets must be calculated from the precise duration of each generated clip. Use `ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 file.mp4` to get exact durations.

### Step 3: Generate narration audio (ElevenLabs TTS)

Generate each scene's narration via ElevenLabs, then concatenate with silence gaps matching transitions.

**Voice**: George (`JBFqnCBsd6RMkjVDRZzb`) -- Warm, Captivating Storyteller (British, middle-aged)

```bash
AUDIO=docs/video/audio/deep-dive
VOICE_ID="JBFqnCBsd6RMkjVDRZzb"
API_KEY="$ELEVENLABS_API_KEY"

# Generate narration for each scene (example for Scene 1)
# Repeat for scenes 01-18 with each scene's text and voice_settings from above
curl -X POST "https://api.elevenlabs.io/v1/text-to-speech/$VOICE_ID" \
  -H "xi-api-key: $API_KEY" \
  -H "Content-Type: application/json" \
  -o $AUDIO/scene01-narration.mp3 \
  -d '{
    "text": "Development teams spend 40 to 60 percent of their time on repetitive toil. Writing boilerplate tests. Fixing linting errors. Coordinating CI/CD pipelines. Addressing the same review comments week after week. It is a tax on every team that ships software.",
    "model_id": "eleven_multilingual_v2",
    "voice_settings": {
      "stability": 0.72,
      "similarity_boost": 0.78,
      "style": 0.35,
      "speed": 0.92
    }
  }'

# ... repeat for scenes 02-18 using each scene's narration text and voice settings ...

# After all 18 narration files are generated, create concat list:
cat > $AUDIO/concat-list.txt << 'EOF'
file 'scene01-narration.mp3'
file 'silence-0.5s.mp3'
file 'scene02-narration.mp3'
file 'silence-0.5s.mp3'
file 'scene03-narration.mp3'
file 'silence-0.7s.mp3'
file 'scene04-narration.mp3'
file 'silence-0.5s.mp3'
file 'scene05-narration.mp3'
file 'silence-0.5s.mp3'
file 'scene06-narration.mp3'
file 'silence-0.4s.mp3'
file 'scene07-narration.mp3'
file 'silence-0.5s.mp3'
file 'scene08-narration.mp3'
file 'silence-0.5s.mp3'
file 'scene09-narration.mp3'
file 'silence-0.5s.mp3'
file 'scene10-narration.mp3'
file 'silence-0.5s.mp3'
file 'scene11-narration.mp3'
file 'silence-0.5s.mp3'
file 'scene12-narration.mp3'
file 'silence-0.4s.mp3'
file 'scene13-narration.mp3'
file 'silence-0.5s.mp3'
file 'scene14-narration.mp3'
file 'silence-0.5s.mp3'
file 'scene15-narration.mp3'
file 'silence-0.7s.mp3'
file 'scene16-narration.mp3'
file 'silence-0.5s.mp3'
file 'scene17-narration.mp3'
file 'silence-0.5s.mp3'
file 'scene18-narration.mp3'
EOF

# Generate silence files
ffmpeg -f lavfi -i anullsrc=r=44100:cl=mono -t 0.4 $AUDIO/silence-0.4s.mp3
ffmpeg -f lavfi -i anullsrc=r=44100:cl=mono -t 0.5 $AUDIO/silence-0.5s.mp3
ffmpeg -f lavfi -i anullsrc=r=44100:cl=mono -t 0.7 $AUDIO/silence-0.7s.mp3

# Concatenate all narration
ffmpeg -f concat -safe 0 -i $AUDIO/concat-list.txt -c:a libmp3lame -q:a 2 $AUDIO/full-narration.mp3
```

### Step 4: Generate sound effects (ElevenLabs SFX)

Generate ambient SFX per scene using each scene's SFX Prompt, plus shared transition swooshes.

```bash
SFX=docs/video/audio/deep-dive/sfx
API_KEY="$ELEVENLABS_API_KEY"

# Transition swooshes (shared across scenes)
curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: $API_KEY" -H "Content-Type: application/json" \
  -o $SFX/swoosh-crossfade.mp3 \
  -d '{"text": "Smooth digital crossfade swoosh, subtle and clean", "duration_seconds": 0.5}'

curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: $API_KEY" -H "Content-Type: application/json" \
  -o $SFX/swoosh-wipeleft.mp3 \
  -d '{"text": "Energetic left-to-right wipe swoosh with slight reverb", "duration_seconds": 0.7}'

curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: $API_KEY" -H "Content-Type: application/json" \
  -o $SFX/swoosh-slideleft.mp3 \
  -d '{"text": "Quick slide transition sound, digital interface movement", "duration_seconds": 0.4}'

# Per-scene ambient SFX (use each scene's SFX Prompt from above)
# Example for Scene 1:
curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: $API_KEY" -H "Content-Type: application/json" \
  -o $SFX/scene01-ambient.mp3 \
  -d '{"text": "Quiet office ambiance with soft electronic undertone, muted keyboard sounds, no music", "duration_seconds": 16.0}'

# ... repeat for scenes 02-18 using each scene's SFX Prompt and duration ...
```

---

## Final Render (ffmpeg)

### Step 5: Mix narration + SFX onto video

```bash
OUTPUT=docs/video/output
AUDIO=docs/video/audio/deep-dive
SFX=docs/video/audio/deep-dive/sfx

# First, mix narration with per-scene ambient SFX
# Narration at full volume, SFX at -18dB (subtle background layer)
ffmpeg -i $AUDIO/full-narration.mp3 -i $SFX/full-sfx-mix.mp3 \
  -filter_complex "[0:a]volume=1.0[narr];[1:a]volume=0.15[sfx];[narr][sfx]amix=inputs=2:duration=longest[mixed]" \
  -map "[mixed]" -c:a libmp3lame -q:a 2 $AUDIO/full-audio-mix.mp3

# Merge video (no audio from Runway) with mixed audio
ffmpeg -i $OUTPUT/deep-dive-video-only.mp4 -i $AUDIO/full-audio-mix.mp3 \
  -c:v copy -c:a aac -b:a 192k \
  -map 0:v:0 -map 1:a:0 \
  -shortest \
  $OUTPUT/deep-dive-final.mp4
```

**Note**: For precise SFX timing, use `adelay` filter to position each scene's ambient SFX and transition swooshes at exact timestamps matching scene cuts. Example:

```bash
# Position Scene 1 ambient at 0ms, Scene 2 ambient at 16250ms, swoosh at 15750ms, etc.
ffmpeg \
  -i $SFX/scene01-ambient.mp3 \
  -i $SFX/swoosh-crossfade.mp3 \
  -i $SFX/scene02-ambient.mp3 \
  -i $SFX/swoosh-crossfade.mp3 \
  -i $SFX/scene03-ambient.mp3 \
  ... \
  -filter_complex \
    "[0:a]adelay=0|0,volume=0.15[s1]; \
     [1:a]adelay=15750|15750,volume=0.3[t1]; \
     [2:a]adelay=16250|16250,volume=0.15[s2]; \
     [3:a]adelay=39250|39250,volume=0.3[t2]; \
     [4:a]adelay=39750|39750,volume=0.15[s3]; \
     ... \
     [s1][t1][s2][t2][s3]...amix=inputs=N:duration=longest[out]" \
  -map "[out]" -c:a libmp3lame -q:a 2 $SFX/full-sfx-mix.mp3
```

### Step 6: Quality check

```bash
# Verify final output
ffprobe -v error -show_entries format=duration,size,bit_rate \
  -show_entries stream=codec_name,width,height,r_frame_rate \
  -of default=noprint_wrappers=1 \
  docs/video/output/deep-dive-final.mp4
```

### Expected Output

- **File**: `docs/video/output/deep-dive-final.mp4`
- **Resolution**: 1920x1080 (1080p) -- upscaled from Runway's 1280x720
- **Frame rate**: 24 fps
- **Codec**: H.264 video, AAC audio
- **Duration**: ~5 minutes 57 seconds (357 seconds)
- **Audio**: ElevenLabs narration (George voice) + ElevenLabs SFX ambient layers (no background music)
- **Transitions**: 17 inter-scene xfade transitions + fade in/out (ffmpeg)

### Production Summary

| Metric | Value |
|--------|-------|
| Total scenes | 18 |
| Total video clips (Runway Gen4 Turbo via Freepik) | 43 |
| Total transitions (ffmpeg xfade) | 19 (17 inter-scene + fade in + fade out) |
| Narration segments (ElevenLabs TTS, George voice) | 18 |
| Sound effect layers (ElevenLabs SFX) | 18 ambient + 19 transition swooshes |
| Video duration | ~5:57 |
| Estimated Runway cost (~366s @ $0.12/s) | ~$43.92 |
| Section A (Problem) | Scenes 1-3, ~56s |
| Section B (Intro) | Scenes 4-6, ~50s |
| Section C (How It Works) | Scenes 7-12, ~122s |
| Section D (Differentiator) | Scenes 13-15, ~70s |
| Section E (Architecture) | Scenes 16-18, ~68s |

### API Dependencies

| Service | Provider | Endpoint | Auth Header |
|---------|----------|----------|-------------|
| Image-to-Video | Runway Gen4 Turbo via Freepik | `POST /v1/ai/image-to-video/runway-4-5` | `x-freepik-api-key` |
| Task Status | Freepik | `GET /v1/ai/image-to-video/runway-4-5/{task_id}` | `x-freepik-api-key` |
| Narration TTS | ElevenLabs | `POST /v1/text-to-speech/{voice_id}` | `xi-api-key` |
| Sound Effects | ElevenLabs | `POST /v1/sound-generation` | `xi-api-key` |
| Voice List | ElevenLabs | `GET /v1/voices` | `xi-api-key` |
| Stitching/Mix | ffmpeg (local) | N/A | N/A |

