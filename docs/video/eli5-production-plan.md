# ELI5 Video Production Plan

## TODO
- [x] Scene 1: The Developer's Day (8s)
- [x] Scene 2: What If? (5s)
- [x] Scene 3: Meet Tamma (8s)
- [x] Scene 4: The Autonomous Loop (10s, 2 clips)
- [x] Scene 5: Pick Your AI (7s)
- [x] Scene 6: Works Everywhere (7s)
- [x] Scene 7: Built-In Quality (8s)
- [x] Scene 8: Complete Transparency (7s)
- [x] Scene 9: It Maintains Itself (8s)
- [x] Scene 10: Get Started (7s)
- [x] Stitching & Transitions
- [x] Final Render

---

## Prerequisites

```bash
# Tools required
apt install ffmpeg jq curl bc   # ffmpeg 6+, utilities for API scripting

# Working directories
mkdir -p docs/video/output/clips
mkdir -p docs/video/output/audio
mkdir -p docs/video/output/sfx
mkdir -p docs/video/output/assembly

# Environment variables
export FREEPIK_API_KEY="your-freepik-api-key"              # Runway Gen4 Turbo via Freepik
export ELEVENLABS_API_KEY="sk_09c1572e21af3e5b20fd3aee0c1628e8c23ce7d707e0da1d"  # Narration TTS + SFX
export ELEVENLABS_VOICE_ID="JBFqnCBsd6RMkjVDRZzb"         # George - Warm, Captivating Storyteller
```

### API Reference

**Video generation** -- Runway Gen4 Turbo via Freepik:
- Generate: `POST https://api.freepik.com/v1/ai/image-to-video/runway-4-5`
- Poll status: `GET https://api.freepik.com/v1/ai/image-to-video/runway-4-5/{task-id}`
- Auth header: `x-freepik-api-key`
- Price: ~$0.12/second of generated video
- Output: video only (no audio track)
- Durations: 5s or 10s
- Aspect ratios: `1280:720`, `720:1280`, `1104:832`, `832:1104`, `960:960`, `1584:672`

**Narration** -- ElevenLabs Text-to-Speech:
- Endpoint: `POST https://api.elevenlabs.io/v1/text-to-speech/{voice_id}`
- Auth header: `xi-api-key`
- Voice: George (`JBFqnCBsd6RMkjVDRZzb`) -- warm, captivating storyteller, British, middle-aged male
- Model: `eleven_multilingual_v2`
- Returns: audio bytes (MP3) directly in response body

**Sound effects** -- ElevenLabs Sound Generation:
- Endpoint: `POST https://api.elevenlabs.io/v1/sound-generation`
- Auth header: `xi-api-key`
- Returns: audio bytes (MP3) directly in response body

**Post-production** -- ffmpeg:
- Stitch clips with xfade transitions
- Mix narration audio timed to scene cuts
- Layer sound effects at transition points
- Final render to MP4

**Output specs**: 1280x720, 24fps, H.264, AAC audio, ~80 seconds total

---

## Scene 1: The Developer's Day

**Duration**: 8 seconds | **Script reference**: Scene 1 "The Pain"

### IMAGES

**Start frame (A)**: `docs/video/scenes/eli5/01-the-pain.png`
- A developer sits hunched at a desk in a dark room, head in hands, clearly overwhelmed. Three monitors surround him. The air is filled with floating holographic error panels in red and teal/blue: "CRITICAL ERROR", "SYSTEM FAILURE", "CONNECTION LOST", "DEBUGGING FAILED", "SYNTAX ERROR". Warning triangle icons pulse on multiple panels. To the right, tall translucent panels show endless task lists and code review items in small text. A loading spinner sits on the center monitor. A coffee cup sits on the desk. The overall palette is dark navy background with red (#EF4444) error accents and muted teal-blue information panels. The mood is stressful, cluttered, and fatiguing.

**End frame (B)**: `docs/video/scenes/eli5/extra/01B-the-pain.png`
- Five floating translucent screens arranged in a slight arc against a dark navy background with a subtle dot-grid pattern. The center screen shows a circular loading indicator at "98% - Loading..." with a red progress bar and text "Waiting for resources... Syncing pipelines...". The left two screens display stacked code review panels and CI pipeline logs with red failure indicators and warning icons. The right two screens show chat/review comment threads with red notification badges and a calendar/kanban board with red-highlighted blocked items. No human figure is visible -- this is pure "wall of waiting screens." The mood shifts from personal overwhelm to systemic, impersonal delay.

### VIDEO CLIP

- **API**: Runway Gen4 Turbo via Freepik (`POST /v1/ai/image-to-video/runway-4-5`)
- **image**: `docs/video/scenes/eli5/01-the-pain.png` (base64-encoded or public HTTPS URL)
- **prompt**: "Cinematic slow dolly-out from the developer's hunched figure at the desk, gradually revealing more floating error panels and notification screens. The camera pulls back steadily, creating a sense of the developer being engulfed by the growing chaos. Error panels glow and pulse subtly. The loading spinner on the center monitor rotates. By the end, the human figure has receded and the screens dominate the frame, becoming an impersonal wall of system delays."
- **duration**: 10 (will trim to 8s in post)
- **ratio**: "1280:720"
- **Output**: `docs/video/output/clips/scene01-the-pain.mp4`

```bash
# Generate video
curl -X POST "https://api.freepik.com/v1/ai/image-to-video/runway-4-5" \
  -H "x-freepik-api-key: ${FREEPIK_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "image": "'"$(base64 -w0 docs/video/scenes/eli5/01-the-pain.png)"'",
    "prompt": "Cinematic slow dolly-out from the developer hunched figure at the desk, gradually revealing more floating error panels and notification screens. The camera pulls back steadily, creating a sense of the developer being engulfed by the growing chaos. Error panels glow and pulse subtly. The loading spinner on the center monitor rotates. By the end, the human figure has receded and the screens dominate the frame, becoming an impersonal wall of system delays.",
    "duration": 10,
    "ratio": "1280:720"
  }' | jq -r '.data.task_id'
# Save task_id, then poll:
# curl -s "https://api.freepik.com/v1/ai/image-to-video/runway-4-5/${TASK_ID}" \
#   -H "x-freepik-api-key: ${FREEPIK_API_KEY}" | jq '.data'
# When status is COMPLETED, download from .data.generated[0]
```

### NARRATION

**Text with delivery markup**:
> "Developers spend over **half their day** on work that isn't actually building features. ... Writing boilerplate. Fixing lint errors. **Waiting for CI.** Reviewing the same patterns ... **again** and **again.**"

**Delivery notes**: Start with a matter-of-fact, slightly weary tone. Build subtle frustration through the list. The pause after "features" lets the stat sink in. "Waiting for CI" gets a tired emphasis. The final "again and again" is delivered with resignation, trailing off slightly.

**Voice settings** (ElevenLabs):
- stability: 0.55
- similarity_boost: 0.78
- style: 0.35
- speed: 0.92

**Timing sync**:
- 0.0s-2.5s: "Developers spend over half their day on work that isn't actually building features." (dolly-out reveals full desk)
- 2.5s-3.0s: [breath pause] (more panels slide into view)
- 3.0s-4.5s: "Writing boilerplate. Fixing lint errors." (error panels pulse red)
- 4.5s-6.5s: "Waiting for CI." (loading spinner visible, camera still pulling back)
- 6.5s-8.0s: "Reviewing the same patterns again and again." (screens fill frame, resignation)

### EMOTIONAL ARC

- **Start**: Empathy -- viewer recognizes themselves or their team in this overwhelmed developer
- **End**: Frustration shared -- the impersonal wall of waiting screens makes the pain feel systemic and universal, not just one person's bad day

### TRANSITIONS

- **Entry**: Hard cut from black (or logo splash). The opening shot should feel immediate, dropping the viewer right into the problem.
- **Exit**: 0.5s smooth crossfade into Scene 2's purple glow, the red tones bleeding into purple as the mood shifts from pain to possibility.

### SOUND

Sound effects to generate via ElevenLabs Sound Generation API:
- Quiet ambient office hum (background layer)
- Faint mechanical keyboard tapping that slows and stops
- Soft digital notification "ding" sounds, slightly distorted, layered -- creating an oppressive texture
- A subtle low-frequency drone building underneath, conveying mounting stress
- No music in this scene -- the silence between sounds should feel heavy

---

## Scene 2: What If?

**Duration**: 5 seconds | **Script reference**: Scene 2 "The Question"

### IMAGES

**Start frame (A)**: `docs/video/scenes/eli5/02-the-question.png`
- A large glowing purple (#7B61FF) question mark dominates the center of the frame, radiating soft violet light outward in a halo effect. The background is dark navy (#0F0F1A) with a subtle circuit-board trace pattern visible in darker navy tones. Four faint development concept icons are positioned around the question mark: upper-left shows a Git branching diagram (nodes connected by arrows), upper-right shows a test tube with liquid drops (testing), lower-left shows a checkmark (approval/passing), and lower-right shows a merge/fork arrow icon (Git merge). These surrounding icons are rendered in muted teal-gray outlines, dim but clearly recognizable. Faint code text and UI elements are ghosted into the circuit-board background. The mood is curious, contemplative, and transitional -- the question hangs in space.

**End frame**: Single frame (no B frame). Motion is achieved through slow zoom and glow intensification on the A frame.

### VIDEO CLIP

- **API**: Runway Gen4 Turbo via Freepik (`POST /v1/ai/image-to-video/runway-4-5`)
- **image**: `docs/video/scenes/eli5/02-the-question.png` (base64-encoded or public HTTPS URL)
- **prompt**: "Cinematic slow push-in toward the glowing purple question mark at center. The purple glow intensifies and pulses gently, radiating outward. The four surrounding development icons (Git branch, test tube, checkmark, merge arrow) drift very slightly inward toward the question mark as if drawn to it. Circuit-board traces in the background shimmer faintly. The overall light level increases subtly, as if dawn is breaking on a new idea."
- **duration**: 5
- **ratio**: "1280:720"
- **Output**: `docs/video/output/clips/scene02-the-question.mp4`

### NARRATION

**Text with delivery markup**:
> "What if an AI could handle **all of that** for you -- ... from the moment an issue is assigned ... to the moment code is **merged?**"

**Delivery notes**: Warm, hopeful, conspiratorial tone -- like sharing a secret. The "what if" is gentle, inviting. "All of that" carries slight wonder. The pause before "from the moment" builds anticipation. "Merged" lands with quiet confidence, a period not an exclamation.

**Voice settings** (ElevenLabs):
- stability: 0.50
- similarity_boost: 0.80
- style: 0.40
- speed: 0.88

**Timing sync**:
- 0.0s-1.8s: "What if an AI could handle all of that for you --" (push-in begins, glow pulses)
- 1.8s-2.3s: [beat pause] (icons drift inward)
- 2.3s-3.8s: "from the moment an issue is assigned" (glow intensifying)
- 3.8s-5.0s: "to the moment code is merged?" (peak glow, hold on tight frame)

### EMOTIONAL ARC

- **Start**: Curiosity sparked -- the viewer leans in, the pain of Scene 1 still fresh
- **End**: Hope kindled -- the question feels like a genuine possibility, not a gimmick. The rising glow mirrors the viewer's rising interest.

### TRANSITIONS

- **Entry**: 0.7s wipe with purple glow from Scene 1. The red error tones dissolve into the purple question mark's light, visually answering chaos with possibility.
- **Exit**: 0.5s smooth crossfade into Scene 3. The purple glow carries forward into the Tamma logo reveal, maintaining color continuity.

### SOUND

Sound effects to generate via ElevenLabs Sound Generation API:
- A soft, shimmering synth pad -- warm and ethereal, a single sustained chord that rises in pitch very slightly over 5 seconds
- A gentle "whoosh" or breath-like sound at the start as the question mark's glow intensifies
- No percussion, no rhythm. The sound should feel like a door opening. Silence between sounds creates space for the narration to breathe.

---

## Scene 3: Meet Tamma

**Duration**: 8 seconds | **Script reference**: Scene 3 "Introducing Tamma"

### IMAGES

**Start frame (A)**: `docs/video/scenes/eli5/03-meet-tamma.png`
- The Tamma logo takes center stage: a bold gold (#F59E0B) stylized "T" letter mark with angular, shield-like geometry, enclosed within a glowing purple (#7B61FF) circular ring. Radial light beams shoot outward from behind the logo in alternating purple and green (#10b981) shafts, creating a dramatic starburst effect. Below the logo, "T A M M A" is displayed in clean white spaced-out sans-serif lettering, with the tagline "It is Done" in italicized white text beneath. The background is dark navy (#0F0F1A) with a subtle hexagonal grid pattern visible in the darker areas. Small sparkle/star particles float in the light beams. The mood is bold, premium, and confident -- a hero moment.

**End frame (B)**: `docs/video/scenes/eli5/extra/03B-meet-tamma.png`
- A dynamic action shot: the Tamma coin/badge rotates at a three-quarter angle, now displaying Arabic calligraphy of "tamm" in gold within the purple ring. The badge is tilted and in motion, with streaks of green and purple code-text trailing behind it like a comet tail. Below the badge, concentric glowing rings in cyan and purple radiate outward and downward, creating an energy vortex effect. Small code symbols (brackets, arrows, checkmarks) scatter outward from the impact point. The circuit-board background is more visible. The mood shifts from static reveal to kinetic energy -- Tamma is not just a logo, it is a force in motion.

### VIDEO CLIP

- **API**: Runway Gen4 Turbo via Freepik (`POST /v1/ai/image-to-video/runway-4-5`)
- **image**: `docs/video/scenes/eli5/03-meet-tamma.png` (base64-encoded or public HTTPS URL)
- **prompt**: "Cinematic logo reveal. The Tamma gold T logo and purple ring hold center frame as radial light beams rotate very slowly clockwise. At 3 seconds, the logo begins a slow dimensional rotation, gradually revealing Arabic calligraphy on its reverse side. The light beams transition from a starburst pattern to trailing streaks as if the logo is gathering momentum. By 6 seconds, concentric energy rings begin expanding outward from below the badge. Code symbols scatter gently from the center. The rotation settles at a three-quarter angle."
- **duration**: 10 (will trim to 8s in post)
- **ratio**: "1280:720"
- **Output**: `docs/video/output/clips/scene03-meet-tamma.mp4`

### NARRATION

**Text with delivery markup**:
> "Meet **Tamma** -- ... an autonomous development platform. The name comes from the Arabic word meaning ... '**it is done.**' ... And that is **exactly** what Tamma does. ... It gets things **done.**"

**Delivery notes**: Warm introduction, as if presenting someone important. "Tamma" is pronounced clearly with pride (TAM-ma, stress on first syllable). The pause before "an autonomous development platform" lets the name register. "It is done" is delivered with quiet reverence -- this is the meaning. The final "It gets things done" is confident and slightly punchy, landing with authority.

**Voice settings** (ElevenLabs):
- stability: 0.60
- similarity_boost: 0.82
- style: 0.45
- speed: 0.90

**Timing sync**:
- 0.0s-1.5s: "Meet Tamma --" (logo holds center, light beams radiate, name appears)
- 1.5s-3.0s: "an autonomous development platform." (beams rotate slowly)
- 3.0s-4.5s: "The name comes from the Arabic word meaning" (logo begins rotation)
- 4.5s-5.5s: "'it is done.'" (Arabic calligraphy becomes visible, beat pause)
- 5.5s-6.5s: [pause] "And that is exactly what Tamma does." (energy rings begin)
- 6.5s-8.0s: "It gets things done." (full kinetic energy, confident landing)

### EMOTIONAL ARC

- **Start**: Intrigue and admiration -- the viewer encounters a premium, confident brand for the first time
- **End**: Trust and anticipation -- the meaning behind the name ("it is done") creates a promise. The kinetic energy of the end frame conveys that this is not vaporware, it is something active and powerful.

### TRANSITIONS

- **Entry**: 0.5s smooth crossfade from Scene 2. The purple glow of the question mark dissolves into the purple ring of the Tamma logo -- the question is being answered.
- **Exit**: 0.4s slide-left transition into Scene 4. The kinetic energy of the rotating badge carries momentum into the pipeline visualization.

### SOUND

Sound effects to generate via ElevenLabs Sound Generation API:
- A deep, resonant "boom" or gong hit at 0.0s as the logo appears -- not aggressive, but authoritative
- A warm orchestral pad swell underneath (strings or synth-strings)
- At 3.0s when the rotation begins, a soft crystalline chime
- At 5.5s when "it gets things done" begins, a subtle bass pulse kicks in, adding momentum
- The overall sound should feel like a premium product launch -- confident, polished, aspirational

---

## Scene 4: The Autonomous Loop

**Duration**: 10 seconds (2 clips: 5s + 5s) | **Script reference**: Scene 4 "How It Works"

### IMAGES

**Start frame (A)**: `docs/video/scenes/eli5/04-autonomous-loop.png`
- A horizontal pipeline of seven connected glowing nodes against a dark cyberpunk-tinged navy background. From left to right: (1) a pink/magenta issue ticket icon, (2) a yellow lightbulb/plan icon, (3) a blue code brackets "</>" icon, (4) a teal test beaker/flask icon, (5) a blue headphones/PR icon, (6) an orange wrench/tools fix icon, and (7) a large green checkmark icon labeled "DEPLOYED" with green starburst rays. The nodes are connected by glowing purple pipeline tubes with small energy pulses traveling along them. In the bottom-left, a small human silhouette figure stands at a terminal/control panel, pressing a button to initiate the flow. The background features a faint cityscape with floating holographic monitors. The mood is organized, futuristic, and powerful -- chaos has been replaced by a clear automated flow.

**Mid frame (B)**: `docs/video/scenes/eli5/extra/04B-autonomous-loop.png`
- A close-up detail view of the pipeline between two nodes: the code brackets "</>" node on the left and the test beaker node on the right. Both are rendered as large metallic junction boxes connected to vertical and horizontal purple glowing pipes. Between them, streams of translucent blue code text flow from left to right in curved lines, representing generated code being fed into testing. Above the beaker node, green checkmark icons and green lightning bolts erupt upward, indicating tests passing. The perspective is tighter and more industrial, emphasizing the physical-feeling infrastructure of the pipeline. Dark navy background with industrial pipe detailing at the edges.

**End frame (C)**: `docs/video/scenes/eli5/extra/04C-autonomous-loop.png`
- A triumphant close-up of the final "MERGED" result: a large green-glowing rounded-rectangle badge with a bold green checkmark icon and the word "MERGED" in white text below it. The badge sits on a circuit-board surface with traces radiating outward. Green and purple particle confetti explodes from behind the badge. In the background, slightly blurred, a Git merge/branch icon floats, and faint code editor windows are visible. The mood is celebratory and conclusive -- the pipeline has delivered its result. Green (#10b981) dominates with purple accents.

### VIDEO CLIPS

**Clip 4a** (first half):
- **API**: Runway Gen4 Turbo via Freepik (`POST /v1/ai/image-to-video/runway-4-5`)
- **image**: `docs/video/scenes/eli5/04-autonomous-loop.png` (base64-encoded or public HTTPS URL)
- **prompt**: "Cinematic tracking shot along the horizontal pipeline from left to right. Starting wide on the full seven-node pipeline with the human figure pressing the button, the camera begins a smooth dolly right, following glowing purple energy pulses as they travel through the pipeline tubes from the issue node toward the code and test nodes. As the camera moves right, it gradually pushes in, transitioning from the wide overview to a close-up of the code-to-test junction. Code text streams flow between the two nodes. Green checkmarks erupt from the test beaker."
- **duration**: 5
- **ratio**: "1280:720"
- **Output**: `docs/video/output/clips/scene04a-autonomous-loop.mp4`

**Clip 4b** (second half):
- **API**: Runway Gen4 Turbo via Freepik (`POST /v1/ai/image-to-video/runway-4-5`)
- **image**: `docs/video/scenes/eli5/extra/04B-autonomous-loop.png` (base64-encoded or public HTTPS URL)
- **prompt**: "Continuing the pipeline journey. From the close-up of code flowing into the test beaker with green checkmarks erupting, the camera pulls back slightly and continues its dolly-right movement, passing through the remaining pipeline stages. The energy pulse accelerates. At 3 seconds, the camera pushes in dramatically on the final MERGED badge as it materializes with a burst of green light and particle confetti. The checkmark icon glows intensely. The word MERGED appears with authority."
- **duration**: 5
- **ratio**: "1280:720"
- **Output**: `docs/video/output/clips/scene04b-autonomous-loop.mp4`

### NARRATION

**Text with delivery markup**:
> "You assign an issue. ... **Tamma reads it**, plans a solution, **writes the code**, runs the tests, creates a pull request, fixes any problems, and **merges it.** ... All automatically. ... You stay in control of the **big decisions.**"

**Delivery notes**: Energetic and rhythmic, like a guided tour. Each step in the pipeline gets its own beat, delivered with building momentum. "Tamma reads it" starts the acceleration. The list builds pace: "plans... writes... runs... creates... fixes... merges" -- each word slightly faster than the last. "All automatically" is delivered with a satisfied pause afterward. "You stay in control of the big decisions" is warmer and reassuring, a deliberate deceleration.

**Voice settings** (ElevenLabs):
- stability: 0.52
- similarity_boost: 0.80
- style: 0.40
- speed: 0.95

**Timing sync**:
- 0.0s-1.5s: "You assign an issue." (human figure presses button, energy pulse starts) [Clip 4a]
- 1.5s-2.0s: [brief pause] "Tamma reads it, plans a solution," (pulse travels through first two nodes)
- 2.0s-4.0s: "writes the code, runs the tests," (camera tracks to code/test junction, streams flow)
- 4.0s-5.5s: "creates a pull request, fixes any problems," (transition between clips, continuing pipeline) [Clip 4a -> 4b]
- 5.5s-7.0s: "and merges it." (MERGED badge materializes, green burst)
- 7.0s-8.0s: [pause] "All automatically." (confetti settles, moment of satisfaction)
- 8.0s-10.0s: "You stay in control of the big decisions." (camera holds on MERGED, tone warms)

### EMOTIONAL ARC

- **Start**: Excitement -- the viewer sees the entire pipeline laid out and understands the scope of what Tamma automates
- **End**: Satisfaction with reassurance -- the MERGED celebration delivers a dopamine hit, and "you stay in control" prevents any anxiety about AI autonomy

### TRANSITIONS

- **Entry**: 0.4s slide-left from Scene 3. The kinetic energy of the rotating Tamma badge flows into the pipeline visualization's left-to-right motion.
- **Between clips**: Seamless cut between Clip 4a and 4b at the code/test junction -- the end frame of 4a is the start frame of 4b, so no transition is needed.
- **Exit**: 0.5s smooth crossfade into Scene 5. The green glow of the MERGED badge softens as the AI hub visualization fades in.

### SOUND

Sound effects to generate via ElevenLabs Sound Generation API:
- An upbeat electronic rhythm -- not a full song, but a rhythmic pulse that matches the pipeline's energy flow
- Soft "whoosh" sounds as energy pulses travel between nodes
- A gentle "ding" for each pipeline stage the pulse passes through
- At the MERGED moment (second 5.5-6), a satisfying achievement sound -- a crystalline chime combined with a subtle bass drop
- The confetti moment gets a gentle sparkle/shimmer sound
- The rhythm slows and settles for the reassuring closing line

---

## Scene 5: Pick Your AI

**Duration**: 7 seconds | **Script reference**: Scene 5 "Your Choice of AI"

### IMAGES

**Start frame (A)**: `docs/video/scenes/eli5/05-pick-your-ai.png`
- A central hub circle glowing in intense purple (#7B61FF) with a brain/AI icon inside, surrounded by a pulsing purple energy ring. Eight provider circles are arranged in a radial pattern around the hub, each connected by glowing circuit-board-style traces. Each provider is represented by a distinct abstract geometric icon in its own colored circle: upper-left: cyan triangle (Anthropic-style), upper-right: concentric gold circles, right: teal spiral, lower-right: pink/magenta ring segments, lower: lavender hexagonal cluster, lower-left: red diamond/gem, left: orange multi-pointed star, and middle-left: green hexagon. The connection lines pulse with energy flowing between hub and providers. A faint cityscape backdrop with vertical light beams adds depth. The overall composition conveys choice, connectivity, and multi-provider flexibility. Dark navy background.

**End frame (B)**: `docs/video/scenes/eli5/extra/05B-pick-your-ai.png`
- A more subdued, architectural version of the hub-and-spoke diagram. The central hub is now a layered hexagonal shape in purple (#7B61FF) with "7B61FF" text visible inside. Eight provider circles are connected by dark circuit-board traces, but now only ONE provider (the upper-left triangle) is actively lit in bright cyan with a strong glow and an "Active" label with checkmark badge. A swap/switch icon appears near the active connection. All other seven provider circles are dimmed to dark outlines (circle, square, pentagon, star shapes visible but unlit). The single active glowing connection line runs from the triangle to the central hub. The mood has shifted from "look at all these options" to "you pick one, and it lights up." Dark navy background with subtle circuit traces.

### VIDEO CLIP

- **API**: Runway Gen4 Turbo via Freepik (`POST /v1/ai/image-to-video/runway-4-5`)
- **image**: `docs/video/scenes/eli5/05-pick-your-ai.png` (base64-encoded or public HTTPS URL)
- **prompt**: "Cinematic slow zoom into the central AI hub. All eight provider circles initially glow and pulse with energy. Over 4 seconds, seven of the eight providers gracefully dim and fade to dark outlines while one provider (upper-left triangle) intensifies its glow dramatically. A bright cyan energy beam solidifies along the connection line from the selected provider to the central hub. An 'Active' badge with checkmark materializes next to the selected provider. A swap icon appears, suggesting easy switching. The central hub absorbs the energy and glows brighter. The composition simplifies from busy to focused."
- **duration**: 10 (will trim to 7s in post)
- **ratio**: "1280:720"
- **Output**: `docs/video/output/clips/scene05-pick-your-ai.mp4`

### NARRATION

**Text with delivery markup**:
> "Tamma works with the AI **you** want. ... Claude, GPT, Gemini, open-source models -- even **local LLMs** on your own machine. ... No vendor lock-in. ... **Your choice.**"

**Delivery notes**: Empowering and inclusive. "You" is stressed to make it personal. The list of providers is delivered casually, almost as an aside -- the point is not the names but the breadth. "Local LLMs on your own machine" gets extra emphasis because it is the surprising one. "No vendor lock-in" is matter-of-fact. "Your choice" is the landing -- two words, delivered with quiet confidence and a slight smile in the voice.

**Voice settings** (ElevenLabs):
- stability: 0.55
- similarity_boost: 0.78
- style: 0.38
- speed: 0.93

**Timing sync**:
- 0.0s-1.8s: "Tamma works with the AI you want." (all providers glowing equally)
- 1.8s-2.3s: [beat] (providers begin dimming)
- 2.3s-4.0s: "Claude, GPT, Gemini, open-source models --" (clockwise dimming sequence)
- 4.0s-5.2s: "even local LLMs on your own machine." (single provider lights up brightly)
- 5.2s-5.8s: "No vendor lock-in." (Active badge appears)
- 5.8s-7.0s: [pause] "Your choice." (swap icon materializes, hold on clean composition)

### EMOTIONAL ARC

- **Start**: Impressed by breadth -- the eight glowing providers convey a rich ecosystem
- **End**: Empowered -- the viewer feels ownership and control. The "your choice" message lands because they have just watched the selection happen visually. No anxiety about being locked in.

### TRANSITIONS

- **Entry**: 0.5s smooth crossfade from Scene 4. The green glow of MERGED softens as the purple AI hub fades in.
- **Exit**: 0.5s smooth crossfade into Scene 6. The hub-and-spoke pattern morphs into the platform cards arrangement.

### SOUND

Sound effects to generate via ElevenLabs Sound Generation API:
- Soft electronic ambient texture continues from Scene 4 but calmer
- A gentle "select" sound (like a soft UI click) each time a provider dims
- When the active provider lights up at 4s, a brighter chime with a subtle reverb tail
- The "Active" badge appearance gets a soft confirmation tone
- Overall mood is clean, modern, and reassuring -- like a well-designed settings screen

---

## Scene 6: Works Everywhere

**Duration**: 7 seconds | **Script reference**: Scene 6 "Your Choice of Platform"

### IMAGES

**Start frame (A)**: `docs/video/scenes/eli5/06-works-everywhere.png`
- Seven floating platform cards arranged in an upward-facing arc/fan above a central Tamma node at the bottom. Each card is a dark rounded rectangle with a 3D perspective tilt, containing a different abstract Git platform icon: a Y-shaped branch (far left), a cube with nodes, interconnected dots, a diamond shape, a swirl, a circuit-tree, and stacked chevrons (far right). All cards are connected to the Tamma node at the bottom center by thin green (#10b981) glowing connection lines that converge downward. The Tamma node shows the Tamma logo with a purple (#7B61FF) glow and the word "TAMMA" beneath. Small green energy dots travel along the connection lines. The background is dark navy with faint circuit-board traces. The composition suggests a tree structure -- Tamma is the root connecting to all platforms above.

**End frame (B)**: `docs/video/scenes/eli5/extra/06B-works-everywhere.png`
- Seven platform cards now arranged in a wider arc with more detail visible. Each card now shows a more recognizable Git platform icon: GitHub's octocat silhouette (two cards, one glowing green), a Bitbucket-style branching icon, a GitHub-style mark, a GitLab fox shape, a Gitea tea-leaf shape, and a geometric forge icon. Two cards are actively lit (green glow on left, blue glow on right) with bright energy beams connecting them down to the Tamma hub at bottom center. A bidirectional arrow appears between the two active connections. A floating tooltip card reads "platforms: github-like, gitlab-like" with green checkmarks. The other five cards are visible but dimmer. Circuit-board trace pattern covers the background. The mood emphasizes that Tamma simultaneously connects to multiple platforms and normalizes their differences through a unified interface.

### VIDEO CLIP

- **API**: Runway Gen4 Turbo via Freepik (`POST /v1/ai/image-to-video/runway-4-5`)
- **image**: `docs/video/scenes/eli5/06-works-everywhere.png` (base64-encoded or public HTTPS URL)
- **prompt**: "Cinematic overhead establishing shot looking down at the platform fan. The seven platform cards hover gently with a subtle floating animation. Green energy dots travel down the connection lines toward the central Tamma hub. Over 3 seconds, the abstract icons on the cards morph subtly into more recognizable platform silhouettes. At 4 seconds, two cards (left and right) light up brightly with green and blue glows while the others dim. Bright energy beams solidify along their connections. A bidirectional arrow materializes between the active connections. A tooltip card fades in at lower-right showing platform compatibility with green checkmarks. The Tamma hub pulses with absorbed energy."
- **duration**: 10 (will trim to 7s in post)
- **ratio**: "1280:720"
- **Output**: `docs/video/output/clips/scene06-works-everywhere.mp4`

### NARRATION

**Text with delivery markup**:
> "It also works with **GitHub**, **GitLab**, Gitea, Forgejo, Bitbucket, **Azure DevOps** -- ... wherever your code lives. ... **One tool, every platform.**"

**Delivery notes**: Conversational and confident. The platform names are delivered as a natural list, not a sales pitch. GitHub and GitLab get slight emphasis as the most recognized names. Azure DevOps gets emphasis as the enterprise surprise. "Wherever your code lives" is warm and inclusive. The closing "One tool, every platform" is the tagline moment -- delivered with clean, spaced-out emphasis, each word landing deliberately.

**Voice settings** (ElevenLabs):
- stability: 0.55
- similarity_boost: 0.78
- style: 0.35
- speed: 0.94

**Timing sync**:
- 0.0s-3.5s: "It also works with GitHub, GitLab, Gitea, Forgejo, Bitbucket, Azure DevOps --" (cards float, icons become recognizable, energy flows)
- 3.5s-4.5s: "wherever your code lives." (two cards light up with green/blue glow)
- 4.5s-5.2s: [pause] (bidirectional arrow and tooltip appear)
- 5.2s-7.0s: "One tool, every platform." (full composition visible, clean landing)

### EMOTIONAL ARC

- **Start**: Recognition -- the viewer sees platforms they use daily represented in the visualization
- **End**: Relief and confidence -- "wherever your code lives" eliminates the "but does it work with MY platform?" objection. "One tool, every platform" is a satisfying simplification.

### TRANSITIONS

- **Entry**: 0.5s smooth crossfade from Scene 5. The hub-and-spoke AI pattern morphs into the arc-and-hub platform pattern -- similar structure, different purpose, reinforcing the "works with everything" message.
- **Exit**: 0.5s smooth crossfade into Scene 7. The platform cards dissolve as the quality gate shields materialize.

### SOUND

Sound effects to generate via ElevenLabs Sound Generation API:
- Continuation of the clean electronic ambient from Scene 5
- Soft "connection established" tones as energy dots reach the hub
- When the two cards light up at 4s, a dual-tone confirmation sound (two notes in harmony)
- The tooltip appearance gets a subtle UI pop-in sound
- Overall sonic texture is clean, organized, and professional -- like a well-designed dashboard

---

## Scene 7: Built-In Quality

**Duration**: 8 seconds | **Script reference**: Scene 7 "Quality You Can Trust"

### IMAGES

**Start frame (A)**: `docs/video/scenes/eli5/07-quality-gates.png`
- Three large shield icons arranged horizontally against a dark navy background. Left shield glows blue (#3B82F6) with a test tube/beaker icon inside, labeled "TESTING" below in blue text. Center shield glows purple (#7B61FF) with a padlock icon inside, labeled "SECURITY" in purple text. Right shield glows green (#10b981) with a magnifying glass icon inside, labeled "REVIEW" in green text. Each shield has a green circular checkmark badge at its lower-right. Below the shields, a horizontal conveyor-belt line runs left to right, with small translucent code block documents traveling along it. The code blocks pass through transparent gate barriers positioned under each shield. After passing all three gates, a code block emerges at the far right. Faint code/data visualizations appear in the background corners. The composition is clean, symmetrical, and reassuring -- order and protection.

**End frame (B)**: `docs/video/scenes/eli5/extra/07B-quality-gates.png`
- A dramatic failure-and-recovery scene. Center: a large blue shield with a glowing circuit-pattern lock and a "CODE BLOCK" label on an orange element passing through. Above: a large red X mark with red glowing distortion, indicating a test failure. Upper-right: a circular retry loop icon showing a wrench tool, a red X transitioning to a green checkmark, and circular arrows -- representing Tamma's auto-fix cycle. Far left: a dimmed shield with a green checkmark above it (previous gate already passed). Far right: a dimmed shield (next gate waiting). The mood has shifted from "everything passes cleanly" to "a failure occurred, but Tamma catches and fixes it automatically." Red (#EF4444) accents for the failure, green for the recovery path.

### VIDEO CLIP

- **API**: Runway Gen4 Turbo via Freepik (`POST /v1/ai/image-to-video/runway-4-5`)
- **image**: `docs/video/scenes/eli5/07-quality-gates.png` (base64-encoded or public HTTPS URL)
- **prompt**: "Cinematic tracking shot following a code block along the conveyor belt through three quality gates. The code block approaches the first shield (Testing) and passes through with a green flash -- checkmark confirmed. It continues to the second shield (Security) where it hits a barrier -- a red X materializes with glowing distortion. The conveyor stops briefly. A retry loop icon spins in the upper-right showing a wrench fixing the issue, the X transforming to a checkmark. The code block, now corrected, passes through the Security gate. The first shield dims behind, the third shield (Review) waits ahead. The mood transitions from confidence through brief alarm to recovery."
- **duration**: 10 (will trim to 8s in post)
- **ratio**: "1280:720"
- **Output**: `docs/video/output/clips/scene07-quality-gates.mp4`

### NARRATION

**Text with delivery markup**:
> "Every change passes through **quality gates** -- ... automated tests, security scans, code review. ... If something **fails**, Tamma **fixes it** and tries again. ... If it still cannot fix it, it **asks you** for help. ... Nothing ships **broken.**"

**Delivery notes**: Authoritative and reassuring, like a safety engineer explaining safeguards. "Quality gates" is delivered with weight. The list (tests, scans, review) is matter-of-fact. "Fails" has a brief dramatic drop in tone. "Fixes it" immediately rebounds with confidence. "Asks you for help" is delivered warmly -- this is the human-in-the-loop reassurance. "Nothing ships broken" is the closer -- four words, delivered with absolute certainty, no qualifier.

**Voice settings** (ElevenLabs):
- stability: 0.60
- similarity_boost: 0.80
- style: 0.35
- speed: 0.90

**Timing sync**:
- 0.0s-2.5s: "Every change passes through quality gates -- automated tests, security scans, code review." (code block travels through first gate, checkmarks appear)
- 2.5s-3.0s: [beat] (code block approaches security gate)
- 3.0s-4.5s: "If something fails," (red X materializes, brief alarm)
- 4.5s-6.0s: "Tamma fixes it and tries again." (retry loop spins, wrench works)
- 6.0s-7.0s: "If it still cannot fix it, it asks you for help." (code block passes through, recovery)
- 7.0s-8.0s: "Nothing ships broken." (clean landing, shields dim confidently)

### EMOTIONAL ARC

- **Start**: Trust -- the three shields and systematic gates convey engineering rigor, not AI recklessness
- **End**: Deep reassurance -- the failure-and-recovery sequence is the most important moment. It says "things can go wrong, and Tamma handles it." The "asks you for help" human-in-the-loop moment prevents AI anxiety. "Nothing ships broken" is the promise.

### TRANSITIONS

- **Entry**: 0.5s smooth crossfade from Scene 6. The platform cards dissolve as the shield icons materialize, shifting from "where" to "how well."
- **Exit**: 0.5s smooth crossfade into Scene 8. The shields dissolve as the timeline visualization emerges, shifting from protection to transparency.

### SOUND

Sound effects to generate via ElevenLabs Sound Generation API:
- Mechanical conveyor belt hum underneath -- subtle, industrial
- Gate passage sounds: a clean "verification confirmed" two-tone beep with each green checkmark
- At the failure moment (3-4s): a sharp but not jarring alert tone -- more like a system notification than an alarm
- Retry loop: a whirring mechanical sound like a machine recalibrating
- Recovery passage: the confirmation beep returns, slightly more triumphant
- Final "nothing ships broken" moment gets a deep, satisfying bass note of finality

---

## Scene 8: Complete Transparency

**Duration**: 7 seconds | **Script reference**: Scene 8 "The Audit Trail"

### IMAGES

**Start frame (A)**: `docs/video/scenes/eli5/08-audit-trail.png`
- A vertical timeline runs down the center of the frame, rendered as a glowing purple (#7B61FF) line with gold (#F59E0B) gear/timestamp markers at each node. Four event cards branch alternately left and right from the timeline nodes: upper-left: "ISSUE ASSIGNED" in a gray/white card with "DAY 01 - 09:00 AM" timestamp and a user icon. Upper-right: "CODE GENERATED" in a green-bordered card with "DAY 01 - 02:30 PM" and a detail icon. Lower-left: "TESTS PASSED" in a teal card with "DAY 02 - 11:15 AM", a shield icon, and a progress bar. Lower-right: "PR MERGED" in a red/pink-bordered card with "DAY 02 - 05:45 PM" and a checkmark. In the upper-right corner, a translucent rewind/time-travel icon (clock with counter-clockwise arrow) floats. The background is dark navy with HUD-style frame borders, circuit patterns, and faint data visualizations. The mood is meticulous, organized, and trustworthy -- every action is recorded.

**End frame (B)**: `docs/video/scenes/eli5/extra/08B-audit-trail.png`
- A time-travel drill-down view. The vertical timeline continues down the center as a glowing purple/pink line with particle effects. A large gold cursor/pointer hovers at a specific point on the timeline, with golden sparkle trails. The "CODE GENERATED" event has been expanded into a detailed inspection panel on the right side: a bordered card showing "TIMESTAMP: 2024-10-26T14:32:18.456Z", a code diff with green "+" additions and red "-" deletions of a `processData()` function, a robot/AI avatar icon, and "PROVIDER: GITHUB_COPILOT_API" at the bottom. On the left side of the timeline, stacked event labels ("FILE UPLOAD", "USER LOGIN") show other events in the stream. Above, grayed-out future events ("FUTURE EVENT", "SYSTEM UPDATE", "ANALYTICS REPORT") indicate the timeline extends further. Upper-left: a glowing infinity/rewind loop icon in blue and purple. The mood is investigative -- the viewer is rewinding time and inspecting exactly what the AI did.

### VIDEO CLIP

- **API**: Runway Gen4 Turbo via Freepik (`POST /v1/ai/image-to-video/runway-4-5`)
- **image**: `docs/video/scenes/eli5/08-audit-trail.png` (base64-encoded or public HTTPS URL)
- **prompt**: "Cinematic slow scroll down the vertical timeline, with event cards fading into view as the camera descends. The purple timeline glows and pulses. Gold timestamp gears rotate subtly. At 3 seconds, the rewind icon in the upper-right activates -- the camera reverses direction, scrolling back UP the timeline. At 4 seconds, a gold cursor appears and clicks on the CODE GENERATED event. The event card expands dramatically into a detailed inspection panel showing the exact timestamp, code diff, and AI provider used. The other event cards shrink and slide to the periphery. The mood shifts from overview to forensic investigation."
- **duration**: 10 (will trim to 7s in post)
- **ratio**: "1280:720"
- **Output**: `docs/video/output/clips/scene08-audit-trail.mp4`

### NARRATION

**Text with delivery markup**:
> "**Everything** Tamma does is recorded. ... Every decision, every line of code, every approval. ... You can **rewind** to any moment and see **exactly** what happened. ... Full transparency, full **trust.**"

**Delivery notes**: Measured and deliberate, like a legal guarantee. "Everything" opens with weight. The triple repetition ("every decision, every line, every approval") builds rhythm. "Rewind" gets a slight pitch lift -- it is the magical word. "Exactly what happened" is delivered with forensic precision. "Full transparency, full trust" is the paired closer -- two phrases, same structure, delivered with quiet authority.

**Voice settings** (ElevenLabs):
- stability: 0.62
- similarity_boost: 0.80
- style: 0.30
- speed: 0.88

**Timing sync**:
- 0.0s-1.5s: "Everything Tamma does is recorded." (camera scrolls down timeline, cards appear)
- 1.5s-3.0s: "Every decision, every line of code, every approval." (more cards fade in, timeline extends)
- 3.0s-4.5s: "You can rewind to any moment" (rewind activates, camera scrolls back up, gold cursor appears)
- 4.5s-5.5s: "and see exactly what happened." (cursor clicks, detail panel expands with code diff)
- 5.5s-7.0s: "Full transparency, full trust." (hold on detail panel, provider info visible)

### EMOTIONAL ARC

- **Start**: Respect for rigor -- the organized timeline conveys professionalism and accountability
- **End**: Deep trust -- the ability to rewind and inspect exactly what the AI did, down to the code diff and provider used, transforms "trust us" into "verify yourself." This is the scene that wins skeptics.

### TRANSITIONS

- **Entry**: 0.5s smooth crossfade from Scene 7. The quality gates dissolve as the timeline materializes, shifting from "protection" to "accountability."
- **Exit**: 0.5s smooth crossfade into Scene 9. The timeline dissolves as the ouroboros loop emerges, shifting from history to self-sustaining future.

### SOUND

Sound effects to generate via ElevenLabs Sound Generation API:
- A steady, precise ticking sound -- like a high-tech metronome -- underlies the scene, conveying the passage of recorded time
- Each event card appearance gets a soft "log entry" chime
- At the rewind moment (3s), a satisfying "whoosh-reverse" sound, like tape rewinding but digitized
- The cursor click gets a crisp UI interaction sound
- The detail panel expansion gets a gentle "data unfold" sound -- layers of information revealing themselves
- The ticking continues throughout but slows at the end, landing on silence for "full trust"

---

## Scene 9: It Maintains Itself

**Duration**: 8 seconds | **Script reference**: Scene 9 "Self-Maintenance"

### IMAGES

**Start frame (A)**: `docs/video/scenes/eli5/09-self-maintenance.png`
- An ouroboros-inspired circular design dominates the center. A large glowing green (#10b981) circular arrow loop flows clockwise, with the arrow heads visible at the top (pointing right/up) and bottom (pointing left/down). Along the circular path, code symbols and development icons are embedded: curly braces `{}`, angle brackets `<>`, hash marks `#`, forward slashes `//`, checkmarks, clock icons, merge/branch symbols, and document icons -- all rendered in green on a slightly darker ring. Inside the circle, the Tamma "TM" monogram logo glows in purple (#7B61FF) and gold (#F59E0B), with a stylized combination of T and M letterforms. Multiple concentric rings create depth (inner purple, outer green). Small green energy pulse dots travel along the circular path. Circuit-board traces extend from the sides like wings. The background is dark navy (#0F0F1A) with a subtle radial gradient. The mood is elegant, perpetual, and self-sustaining.

**End frame (B)**: `docs/video/scenes/eli5/extra/09B-self-maintenance.png`
- A "SELF-REPAIR CYCLE" visualization. A large semicircular arc (upper half) traces a path from left to right with three key nodes: Left node: a red circle containing a pixel-art bug icon (bug detected), with broken code fragments and error symbols scattering from behind it. Top node: a glowing energy sphere with code brackets `{}` and a wrench icon inside, labeled "Generating Fix" with golden sparks emanating. Right node: a green circle with a bold checkmark inside, labeled "RESOLVED", with clean document/file icons streaming to the right. Blue directional arrows connect the three nodes along the arc. Below the arc at center-bottom, the Tamma logo (a stylized infinity-knot in gold within a purple circle) anchors the composition, with "SELF-REPAIR CYCLE" text beneath. The bottom half shows the arc continuing in purple with directional arrows completing the full circle. The mood has shifted from abstract perpetual motion to a concrete self-repair narrative: detect bug, generate fix, resolve.

### VIDEO CLIP

- **API**: Runway Gen4 Turbo via Freepik (`POST /v1/ai/image-to-video/runway-4-5`)
- **image**: `docs/video/scenes/eli5/09-self-maintenance.png` (base64-encoded or public HTTPS URL)
- **prompt**: "Cinematic slow rotation of the ouroboros loop. Green energy pulses travel clockwise along the circular path, passing code symbols. The loop rotates very slowly. At 3 seconds, the abstract loop begins transforming: the top portion of the circle opens up and flattens into a semicircular arc. Three nodes crystallize along the arc -- a red bug node on the left, a wrench/fix node at the top center with golden sparks, and a green resolved checkmark on the right. The Tamma logo descends to the bottom center. Directional arrows animate between the nodes, showing the flow: detect, fix, resolve. The bottom half of the arc completes in purple, forming the full self-repair cycle."
- **duration**: 10 (will trim to 8s in post)
- **ratio**: "1280:720"
- **Output**: `docs/video/output/clips/scene09-self-maintenance.mp4`

### NARRATION

**Text with delivery markup**:
> "Here is the most **remarkable** part. ... Tamma maintains its **own** codebase. It fixes its **own** bugs. It builds its **own** features. ... That is how we know ... it is **ready** for yours."

**Delivery notes**: This is the "wow" moment. "Here is the most remarkable part" is delivered with genuine excitement held in check -- not hype, but earned awe. The triple "its own" repetition builds with increasing emphasis. "Its own codebase" is matter-of-fact. "Its own bugs" raises an eyebrow. "Its own features" lands with wonder. The final line -- "That is how we know it is ready for yours" -- is the emotional peak of the entire video. It is delivered with absolute confidence and warmth, connecting Tamma's self-capability to the viewer's benefit.

**Voice settings** (ElevenLabs):
- stability: 0.50
- similarity_boost: 0.82
- style: 0.48
- speed: 0.86

**Timing sync**:
- 0.0s-2.0s: "Here is the most remarkable part." (ouroboros rotating, green pulses flowing, building anticipation)
- 2.0s-2.5s: [pause -- let it land] (loop continues)
- 2.5s-3.5s: "Tamma maintains its own codebase." (loop begins morphing)
- 3.5s-4.5s: "It fixes its own bugs." (red bug node materializes on the left)
- 4.5s-5.5s: "It builds its own features." (wrench/fix node sparks at top, green checkmark on right)
- 5.5s-6.5s: [pause] "That is how we know" (directional arrows complete the cycle)
- 6.5s-8.0s: "it is ready for yours." (full SELF-REPAIR CYCLE visible, Tamma logo glowing)

### EMOTIONAL ARC

- **Start**: Intrigued -- the ouroboros loop is visually captivating, the viewer does not yet know what it means
- **End**: Genuinely impressed -- the self-maintenance concept is the single most differentiating claim. The concrete visualization (bug -> fix -> resolved) makes it believable, not hand-wavy. The final "ready for yours" bridges from Tamma's capability to the viewer's direct benefit. This is the emotional climax.

### TRANSITIONS

- **Entry**: 0.7s wipe with purple glow from Scene 8. The timeline dissolves into the ouroboros loop -- time-based history transforms into cyclical self-improvement.
- **Exit**: 0.7s wipe with purple glow into Scene 10. The self-repair cycle contracts and transforms into the final Tamma logo in the CTA. This is a signature transition -- the two most dramatic scenes get the purple wipe treatment.

### SOUND

Sound effects to generate via ElevenLabs Sound Generation API:
- A deep, ambient drone -- spacious and awe-inspiring, like looking into deep space
- A soft rhythmic pulse matching the clockwise rotation of energy pulses
- At the transformation moment (3s), a crystalline "phase shift" sound -- like ice cracking in reverse
- The bug node appearance gets a brief, contained alert tone
- The fix node gets golden sparkle sounds and a mechanical "wrench turning" effect
- The resolved checkmark gets a triumphant confirmation chime
- The final "ready for yours" moment is backed by the deepest, warmest bass note in the video -- finality combined with invitation

---

## Scene 10: Get Started

**Duration**: 7 seconds | **Script reference**: Scene 10 "Call to Action"

### IMAGES

**Start frame (A)**: `docs/video/scenes/eli5/10-cta.png`
- A premium call-to-action screen. Center-top: the word "Tamma" rendered in large, elegant serif-style typography with a purple-to-gold gradient and gold outline, with a soft radial glow behind it in concentric gold and purple rings. Below the logo text: "tamma.dev" in clean white sans-serif text. Below that: the GitHub octocat icon followed by "github.com/meywd/tamma" in smaller muted gray text. The background is a rich gradient from deep navy (#0F0F1A) to deep purple, with faint circuit-board traces in the upper-right and lower-left corners. In the lower-right corner, Arabic calligraphy of "tamm" appears as an elegant gold watermark. The mood is conclusive, premium, and inviting -- a polished final frame.

**End frame (B)**: `docs/video/scenes/eli5/extra/10B-cta.png`
- An action-oriented CTA screen. Left side: a neon browser address bar showing "tamma.dev" being typed (text cursor visible) with a hand/pointer cursor clicking it. Above the address bar, a gold ornamental medallion with Arabic "tamm" calligraphy. Below: a large golden GitHub star badge with the GitHub octocat icon in the center and a blue cursor clicking on it, with golden starburst rays radiating outward -- suggesting "star the repo." Right side: "GET STARTED TODAY" in bold white impact-style text. The background is dark navy-to-purple with scattered bokeh circles in blue and purple. Circuit-board traces in the lower-left. The mood has shifted from "here is who we are" to "take action now" -- the star badge and typed URL invite immediate engagement.

### VIDEO CLIP

- **API**: Runway Gen4 Turbo via Freepik (`POST /v1/ai/image-to-video/runway-4-5`)
- **image**: `docs/video/scenes/eli5/10-cta.png` (base64-encoded or public HTTPS URL)
- **prompt**: "Cinematic logo hold transitioning to call-to-action. The Tamma logo text glows with pulsing purple-gold light, radial rings slowly expanding. At 3 seconds, the composition gracefully rearranges: the centered logo text slides left, the tamma.dev URL transforms into a clickable browser bar with a cursor approaching, and a large GitHub star badge materializes from golden particles on the lower-left. GET STARTED TODAY text types in letter by letter on the right side. Bokeh particles drift gently across the background. The overall motion is confident and inviting, not rushed."
- **duration**: 10 (will trim to 7s in post)
- **ratio**: "1280:720"
- **Output**: `docs/video/output/clips/scene10-cta.mp4`

### NARRATION

**Text with delivery markup**:
> "**Tamma.** ... Autonomous development that is actually **done right.** ... Visit **tamma.dev** ... or find us on **GitHub.**"

**Delivery notes**: The closing statement. "Tamma" is delivered as a standalone word -- the brand name, spoken with pride and finality, like a mic drop. The pause lets it resonate. "Autonomous development that is actually done right" is the tagline -- delivered with measured confidence, each word landing. "Actually" gets slight emphasis, distinguishing Tamma from competitors who overpromise. "Visit tamma.dev" is warm and inviting. "Or find us on GitHub" is casual and friendly, ending on a conversational note rather than a hard sell.

**Voice settings** (ElevenLabs):
- stability: 0.65
- similarity_boost: 0.82
- style: 0.42
- speed: 0.85

**Timing sync**:
- 0.0s-1.5s: "Tamma." (logo glowing center frame, radial rings pulse)
- 1.5s-2.0s: [pause -- brand name resonates]
- 2.0s-4.0s: "Autonomous development that is actually done right." (composition begins rearranging)
- 4.0s-4.5s: [beat]
- 4.5s-5.5s: "Visit tamma.dev" (browser bar appears, cursor clicks)
- 5.5s-7.0s: "or find us on GitHub." (star badge radiates, GET STARTED TODAY visible)

### EMOTIONAL ARC

- **Start**: Satisfaction and admiration -- the premium logo frame rewards the viewer for watching, confirming this is a polished product
- **End**: Motivation to act -- the "GET STARTED TODAY" and clickable elements create urgency without pressure. The viewer should feel invited, not sold to. The Arabic calligraphy watermark adds cultural depth and authenticity.

### TRANSITIONS

- **Entry**: 0.7s wipe with purple glow from Scene 9. The self-repair cycle contracts into the radial glow behind the Tamma logo text -- the cyclical energy funnels into the brand moment.
- **Exit**: 1.0s fade to black. The longest transition in the video -- a luxurious fade that lets the CTA linger. The glow dims slowly, the last things visible are the URLs and the Arabic watermark.

### SOUND

Sound effects to generate via ElevenLabs Sound Generation API:
- A warm, resolving chord -- the musical resolution of the entire video
- The same electronic ambient texture from throughout the video returns but now resolves to a major key
- At the "Tamma" brand name moment, a subtle version of the gong/boom from Scene 3, softer this time -- a callback
- The text typing effect gets soft keyboard clicks
- The star badge radiating gets a gentle shimmer/sparkle
- The final seconds feature the music gently fading, ending with a single sustained note that rings out into the fade-to-black. The silence after the note is intentional -- it leaves space for the viewer to act.

---

## Video Generation Pipeline

### Helper: Generate Video from Image via Freepik Runway Gen4 Turbo

```bash
#!/usr/bin/env bash
# generate-video.sh <image_path> <prompt> <duration> <output_path>
# Submits image-to-video job, polls until complete, downloads result.

set -euo pipefail

IMAGE_PATH="$1"
PROMPT="$2"
DURATION="${3:-10}"
OUTPUT_PATH="$4"

echo ">>> Submitting: $(basename "$IMAGE_PATH") (${DURATION}s)"

# Base64-encode the image
IMAGE_B64=$(base64 -w0 "$IMAGE_PATH")

# Submit generation task
RESPONSE=$(curl -s -X POST "https://api.freepik.com/v1/ai/image-to-video/runway-4-5" \
  -H "x-freepik-api-key: ${FREEPIK_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "image": "'"${IMAGE_B64}"'",
    "prompt": "'"$(echo "$PROMPT" | sed 's/"/\\"/g')"'",
    "duration": '"${DURATION}"',
    "ratio": "1280:720"
  }')

TASK_ID=$(echo "$RESPONSE" | jq -r '.data.task_id')
STATUS=$(echo "$RESPONSE" | jq -r '.data.status')

if [ -z "$TASK_ID" ] || [ "$TASK_ID" = "null" ]; then
  echo "ERROR: Failed to submit task. Response: $RESPONSE"
  exit 1
fi

echo "    Task ID: $TASK_ID (status: $STATUS)"

# Poll until completed
while true; do
  sleep 10
  POLL=$(curl -s "https://api.freepik.com/v1/ai/image-to-video/runway-4-5/${TASK_ID}" \
    -H "x-freepik-api-key: ${FREEPIK_API_KEY}")
  STATUS=$(echo "$POLL" | jq -r '.data.status')
  echo "    Polling... status: $STATUS"

  case "$STATUS" in
    COMPLETED)
      VIDEO_URL=$(echo "$POLL" | jq -r '.data.generated[0]')
      echo "    Downloading: $VIDEO_URL"
      curl -s -L -o "$OUTPUT_PATH" "$VIDEO_URL"
      echo "    Saved: $OUTPUT_PATH"
      break
      ;;
    FAILED)
      echo "ERROR: Task failed. Response: $POLL"
      exit 1
      ;;
    CREATED|IN_PROGRESS)
      continue
      ;;
    *)
      echo "WARNING: Unknown status '$STATUS'. Retrying..."
      continue
      ;;
  esac
done
```

### Generate All 11 Video Clips

```bash
CLIPS="docs/video/output/clips"

# Scene 1 (8s scene, generate 10s, trim later)
bash generate-video.sh \
  "docs/video/scenes/eli5/01-the-pain.png" \
  "Cinematic slow dolly-out from the developer hunched figure at the desk, gradually revealing more floating error panels and notification screens. The camera pulls back steadily, creating a sense of the developer being engulfed by the growing chaos. Error panels glow and pulse subtly. The loading spinner on the center monitor rotates. By the end, the human figure has receded and the screens dominate the frame, becoming an impersonal wall of system delays." \
  10 "${CLIPS}/scene01-the-pain.mp4"

# Scene 2 (5s)
bash generate-video.sh \
  "docs/video/scenes/eli5/02-the-question.png" \
  "Cinematic slow push-in toward the glowing purple question mark at center. The purple glow intensifies and pulses gently, radiating outward. The four surrounding development icons drift very slightly inward toward the question mark as if drawn to it. Circuit-board traces in the background shimmer faintly. The overall light level increases subtly, as if dawn is breaking on a new idea." \
  5 "${CLIPS}/scene02-the-question.mp4"

# Scene 3 (8s scene, generate 10s, trim later)
bash generate-video.sh \
  "docs/video/scenes/eli5/03-meet-tamma.png" \
  "Cinematic logo reveal. The Tamma gold T logo and purple ring hold center frame as radial light beams rotate very slowly clockwise. At 3 seconds, the logo begins a slow dimensional rotation, gradually revealing Arabic calligraphy on its reverse side. The light beams transition from a starburst pattern to trailing streaks as if the logo is gathering momentum. By 6 seconds, concentric energy rings begin expanding outward from below the badge. Code symbols scatter gently from the center. The rotation settles at a three-quarter angle." \
  10 "${CLIPS}/scene03-meet-tamma.mp4"

# Scene 4a (5s)
bash generate-video.sh \
  "docs/video/scenes/eli5/04-autonomous-loop.png" \
  "Cinematic tracking shot along the horizontal pipeline from left to right. Starting wide on the full seven-node pipeline with the human figure pressing the button, the camera begins a smooth dolly right, following glowing purple energy pulses as they travel through the pipeline tubes from the issue node toward the code and test nodes. As the camera moves right, it gradually pushes in, transitioning from the wide overview to a close-up of the code-to-test junction. Code text streams flow between the two nodes. Green checkmarks erupt from the test beaker." \
  5 "${CLIPS}/scene04a-autonomous-loop.mp4"

# Scene 4b (5s)
bash generate-video.sh \
  "docs/video/scenes/eli5/extra/04B-autonomous-loop.png" \
  "Continuing the pipeline journey. From the close-up of code flowing into the test beaker with green checkmarks erupting, the camera pulls back slightly and continues its dolly-right movement, passing through the remaining pipeline stages. The energy pulse accelerates. At 3 seconds, the camera pushes in dramatically on the final MERGED badge as it materializes with a burst of green light and particle confetti. The checkmark icon glows intensely. The word MERGED appears with authority." \
  5 "${CLIPS}/scene04b-autonomous-loop.mp4"

# Scene 5 (7s scene, generate 10s, trim later)
bash generate-video.sh \
  "docs/video/scenes/eli5/05-pick-your-ai.png" \
  "Cinematic slow zoom into the central AI hub. All eight provider circles initially glow and pulse with energy. Over 4 seconds, seven of the eight providers gracefully dim and fade to dark outlines while one provider (upper-left triangle) intensifies its glow dramatically. A bright cyan energy beam solidifies along the connection line from the selected provider to the central hub. An Active badge with checkmark materializes next to the selected provider. A swap icon appears, suggesting easy switching. The central hub absorbs the energy and glows brighter. The composition simplifies from busy to focused." \
  10 "${CLIPS}/scene05-pick-your-ai.mp4"

# Scene 6 (7s scene, generate 10s, trim later)
bash generate-video.sh \
  "docs/video/scenes/eli5/06-works-everywhere.png" \
  "Cinematic overhead establishing shot looking down at the platform fan. The seven platform cards hover gently with a subtle floating animation. Green energy dots travel down the connection lines toward the central Tamma hub. Over 3 seconds, the abstract icons on the cards morph subtly into more recognizable platform silhouettes. At 4 seconds, two cards light up brightly with green and blue glows while the others dim. Bright energy beams solidify along their connections. A bidirectional arrow materializes between the active connections. A tooltip card fades in showing platform compatibility with green checkmarks. The Tamma hub pulses with absorbed energy." \
  10 "${CLIPS}/scene06-works-everywhere.mp4"

# Scene 7 (8s scene, generate 10s, trim later)
bash generate-video.sh \
  "docs/video/scenes/eli5/07-quality-gates.png" \
  "Cinematic tracking shot following a code block along the conveyor belt through three quality gates. The code block approaches the first shield (Testing) and passes through with a green flash -- checkmark confirmed. It continues to the second shield (Security) where it hits a barrier -- a red X materializes with glowing distortion. The conveyor stops briefly. A retry loop icon spins in the upper-right showing a wrench fixing the issue, the X transforming to a checkmark. The code block, now corrected, passes through the Security gate. The first shield dims behind, the third shield waits ahead." \
  10 "${CLIPS}/scene07-quality-gates.mp4"

# Scene 8 (7s scene, generate 10s, trim later)
bash generate-video.sh \
  "docs/video/scenes/eli5/08-audit-trail.png" \
  "Cinematic slow scroll down the vertical timeline, with event cards fading into view as the camera descends. The purple timeline glows and pulses. Gold timestamp gears rotate subtly. At 3 seconds, the rewind icon in the upper-right activates -- the camera reverses direction, scrolling back UP the timeline. At 4 seconds, a gold cursor appears and clicks on the CODE GENERATED event. The event card expands dramatically into a detailed inspection panel showing the exact timestamp, code diff, and AI provider used. The other event cards shrink and slide to the periphery." \
  10 "${CLIPS}/scene08-audit-trail.mp4"

# Scene 9 (8s scene, generate 10s, trim later)
bash generate-video.sh \
  "docs/video/scenes/eli5/09-self-maintenance.png" \
  "Cinematic slow rotation of the ouroboros loop. Green energy pulses travel clockwise along the circular path, passing code symbols. The loop rotates very slowly. At 3 seconds, the abstract loop begins transforming: the top portion of the circle opens up and flattens into a semicircular arc. Three nodes crystallize along the arc -- a red bug node on the left, a wrench/fix node at the top center with golden sparks, and a green resolved checkmark on the right. The Tamma logo descends to the bottom center. Directional arrows animate between the nodes, showing the flow: detect, fix, resolve. The bottom half of the arc completes in purple, forming the full self-repair cycle." \
  10 "${CLIPS}/scene09-self-maintenance.mp4"

# Scene 10 (7s scene, generate 10s, trim later)
bash generate-video.sh \
  "docs/video/scenes/eli5/10-cta.png" \
  "Cinematic logo hold transitioning to call-to-action. The Tamma logo text glows with pulsing purple-gold light, radial rings slowly expanding. At 3 seconds, the composition gracefully rearranges: the centered logo text slides left, the tamma.dev URL transforms into a clickable browser bar with a cursor approaching, and a large GitHub star badge materializes from golden particles on the lower-left. GET STARTED TODAY text types in letter by letter on the right side. Bokeh particles drift gently across the background. The overall motion is confident and inviting, not rushed." \
  10 "${CLIPS}/scene10-cta.mp4"
```

**Cost estimate**: 11 clips at 5-10s each = ~85s total generated video. At $0.12/second = ~$10.20.

---

## Stitching & Transitions

### Transition Summary Table

| From | To | Type | Duration | Notes |
|------|----|------|----------|-------|
| Black | Scene 1 | Hard cut | 0.0s | Immediate -- drop viewer into the problem |
| Scene 1 | Scene 2 | Purple glow wipe | 0.7s | Red error tones bleed into purple question |
| Scene 2 | Scene 3 | Smooth crossfade | 0.5s | Purple question -> purple logo ring |
| Scene 3 | Scene 4 | Slide left | 0.4s | Kinetic energy carries into pipeline |
| Scene 4a | Scene 4b | Seamless cut | 0.0s | Same end/start frame (04B) |
| Scene 4 | Scene 5 | Smooth crossfade | 0.5s | Green MERGED -> purple AI hub |
| Scene 5 | Scene 6 | Smooth crossfade | 0.5s | Hub-spoke AI -> arc-hub platforms |
| Scene 6 | Scene 7 | Smooth crossfade | 0.5s | Platform cards -> quality shields |
| Scene 7 | Scene 8 | Smooth crossfade | 0.5s | Shields -> timeline |
| Scene 8 | Scene 9 | Purple glow wipe | 0.7s | Timeline -> ouroboros (dramatic pair) |
| Scene 9 | Scene 10 | Purple glow wipe | 0.7s | Self-repair -> logo CTA (dramatic pair) |
| Scene 10 | Black | Fade to black | 1.0s | Luxurious lingering fade |

**Total transition time**: ~5.5 seconds (overlapping with scene durations)

### Narration Audio Generation

Generate all 10 narration audio files via ElevenLabs API before stitching:

```bash
# ElevenLabs text-to-speech for each scene
# Voice: George (JBFqnCBsd6RMkjVDRZzb) - Warm, Captivating Storyteller

VOICE_ID="JBFqnCBsd6RMkjVDRZzb"
OUTPUT_DIR="docs/video/output/audio"

# Scene 1 (8s)
curl -X POST "https://api.elevenlabs.io/v1/text-to-speech/${VOICE_ID}" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "Developers spend over half their day on work that isn'\''t actually building features. Writing boilerplate. Fixing lint errors. Waiting for CI. Reviewing the same patterns again and again.",
    "model_id": "eleven_multilingual_v2",
    "voice_settings": {"stability": 0.55, "similarity_boost": 0.78, "style": 0.35, "speed": 0.92}
  }' --output "${OUTPUT_DIR}/narration-scene01.mp3"

# Scene 2 (5s)
curl -X POST "https://api.elevenlabs.io/v1/text-to-speech/${VOICE_ID}" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "What if an AI could handle all of that for you -- from the moment an issue is assigned to the moment code is merged?",
    "model_id": "eleven_multilingual_v2",
    "voice_settings": {"stability": 0.50, "similarity_boost": 0.80, "style": 0.40, "speed": 0.88}
  }' --output "${OUTPUT_DIR}/narration-scene02.mp3"

# Scene 3 (8s)
curl -X POST "https://api.elevenlabs.io/v1/text-to-speech/${VOICE_ID}" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "Meet Tamma -- an autonomous development platform. The name comes from the Arabic word meaning it is done. And that is exactly what Tamma does. It gets things done.",
    "model_id": "eleven_multilingual_v2",
    "voice_settings": {"stability": 0.60, "similarity_boost": 0.82, "style": 0.45, "speed": 0.90}
  }' --output "${OUTPUT_DIR}/narration-scene03.mp3"

# Scene 4 (10s)
curl -X POST "https://api.elevenlabs.io/v1/text-to-speech/${VOICE_ID}" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "You assign an issue. Tamma reads it, plans a solution, writes the code, runs the tests, creates a pull request, fixes any problems, and merges it. All automatically. You stay in control of the big decisions.",
    "model_id": "eleven_multilingual_v2",
    "voice_settings": {"stability": 0.52, "similarity_boost": 0.80, "style": 0.40, "speed": 0.95}
  }' --output "${OUTPUT_DIR}/narration-scene04.mp3"

# Scene 5 (7s)
curl -X POST "https://api.elevenlabs.io/v1/text-to-speech/${VOICE_ID}" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "Tamma works with the AI you want. Claude, GPT, Gemini, open-source models -- even local LLMs on your own machine. No vendor lock-in. Your choice.",
    "model_id": "eleven_multilingual_v2",
    "voice_settings": {"stability": 0.55, "similarity_boost": 0.78, "style": 0.38, "speed": 0.93}
  }' --output "${OUTPUT_DIR}/narration-scene05.mp3"

# Scene 6 (7s)
curl -X POST "https://api.elevenlabs.io/v1/text-to-speech/${VOICE_ID}" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "It also works with GitHub, GitLab, Gitea, Forgejo, Bitbucket, Azure DevOps -- wherever your code lives. One tool, every platform.",
    "model_id": "eleven_multilingual_v2",
    "voice_settings": {"stability": 0.55, "similarity_boost": 0.78, "style": 0.35, "speed": 0.94}
  }' --output "${OUTPUT_DIR}/narration-scene06.mp3"

# Scene 7 (8s)
curl -X POST "https://api.elevenlabs.io/v1/text-to-speech/${VOICE_ID}" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "Every change passes through quality gates -- automated tests, security scans, code review. If something fails, Tamma fixes it and tries again. If it still cannot fix it, it asks you for help. Nothing ships broken.",
    "model_id": "eleven_multilingual_v2",
    "voice_settings": {"stability": 0.60, "similarity_boost": 0.80, "style": 0.35, "speed": 0.90}
  }' --output "${OUTPUT_DIR}/narration-scene07.mp3"

# Scene 8 (7s)
curl -X POST "https://api.elevenlabs.io/v1/text-to-speech/${VOICE_ID}" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "Everything Tamma does is recorded. Every decision, every line of code, every approval. You can rewind to any moment and see exactly what happened. Full transparency, full trust.",
    "model_id": "eleven_multilingual_v2",
    "voice_settings": {"stability": 0.62, "similarity_boost": 0.80, "style": 0.30, "speed": 0.88}
  }' --output "${OUTPUT_DIR}/narration-scene08.mp3"

# Scene 9 (8s)
curl -X POST "https://api.elevenlabs.io/v1/text-to-speech/${VOICE_ID}" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "Here is the most remarkable part. Tamma maintains its own codebase. It fixes its own bugs. It builds its own features. That is how we know it is ready for yours.",
    "model_id": "eleven_multilingual_v2",
    "voice_settings": {"stability": 0.50, "similarity_boost": 0.82, "style": 0.48, "speed": 0.86}
  }' --output "${OUTPUT_DIR}/narration-scene09.mp3"

# Scene 10 (7s)
curl -X POST "https://api.elevenlabs.io/v1/text-to-speech/${VOICE_ID}" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "Tamma. Autonomous development that is actually done right. Visit tamma.dev or find us on GitHub.",
    "model_id": "eleven_multilingual_v2",
    "voice_settings": {"stability": 0.65, "similarity_boost": 0.82, "style": 0.42, "speed": 0.85}
  }' --output "${OUTPUT_DIR}/narration-scene10.mp3"
```

### Sound Effects Generation

Generate transition swooshes and ambient layers via ElevenLabs Sound Generation API:

```bash
SFX_DIR="docs/video/output/sfx"

# Transition swooshes (used between scenes)
curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "A smooth digital whoosh transition sound, like a futuristic UI swipe, clean and modern, 1 second long",
    "duration_seconds": 1.0
  }' --output "${SFX_DIR}/transition-swoosh.mp3"

# Purple glow wipe variant (more dramatic, for Scene 1->2, 8->9, 9->10)
curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "A dramatic ethereal whoosh with a glowing energy sweep, like a magical portal opening, futuristic and warm, 1.5 seconds",
    "duration_seconds": 1.5
  }' --output "${SFX_DIR}/transition-purple-wipe.mp3"

# Subtle ambient tech hum (loopable background layer)
curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "A very subtle, low-frequency ambient technology hum, like the gentle resonance of a data center or quiet server room, barely audible, calming and continuous, 10 seconds",
    "duration_seconds": 10.0
  }' --output "${SFX_DIR}/ambient-tech-hum.mp3"

# Achievement / success chime (for MERGED moment, Scene 4)
curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "A satisfying digital achievement unlocked chime, crystalline and bright with a subtle bass undertone, like completing a quest in a futuristic game, 1 second",
    "duration_seconds": 1.0
  }' --output "${SFX_DIR}/achievement-chime.mp3"

# Logo reveal gong (Scene 3 and callback in Scene 10)
curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "A deep resonant gong hit, authoritative but not aggressive, warm and premium feeling, like a high-end brand reveal, with a long reverb tail, 2 seconds",
    "duration_seconds": 2.0
  }' --output "${SFX_DIR}/logo-gong.mp3"

# Error/failure alert (Scene 7)
curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "A brief, sharp but not jarring digital alert notification tone, like a system warning that is informative rather than alarming, clean and technical, 0.5 seconds",
    "duration_seconds": 0.5
  }' --output "${SFX_DIR}/alert-tone.mp3"

# Rewind whoosh (Scene 8)
curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "A digitized tape rewind sound effect, futuristic and satisfying, like scrolling backward through time in a holographic interface, 1 second",
    "duration_seconds": 1.0
  }' --output "${SFX_DIR}/rewind-whoosh.mp3"

# UI click/select (reusable)
curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "A soft, clean digital UI click or select sound, minimal and modern, like tapping a glass touchscreen, 0.3 seconds",
    "duration_seconds": 0.3
  }' --output "${SFX_DIR}/ui-click.mp3"

# Confirmation beep (reusable for quality gates)
curl -X POST "https://api.elevenlabs.io/v1/sound-generation" \
  -H "xi-api-key: ${ELEVENLABS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "A clean two-tone verification confirmed beep, like a security checkpoint approving access, professional and reassuring, 0.5 seconds",
    "duration_seconds": 0.5
  }' --output "${SFX_DIR}/confirmation-beep.mp3"
```

### Step 1: Prepare Clips with Correct Duration

Ensure each Runway-generated clip matches the target duration. Runway Gen4 Turbo only supports 5s or 10s durations, so we trim longer clips down:

```bash
CLIPS="docs/video/output/clips"
ASSEMBLY="docs/video/output/assembly"

# Trim each clip to exact scene duration
# Runway outputs at its native resolution; we will scale in a later step if needed

ffmpeg -i "${CLIPS}/scene01-the-pain.mp4" -t 8 -c copy "${ASSEMBLY}/s01.mp4"
ffmpeg -i "${CLIPS}/scene02-the-question.mp4" -t 5 -c copy "${ASSEMBLY}/s02.mp4"
ffmpeg -i "${CLIPS}/scene03-meet-tamma.mp4" -t 8 -c copy "${ASSEMBLY}/s03.mp4"
ffmpeg -i "${CLIPS}/scene04a-autonomous-loop.mp4" -t 5 -c copy "${ASSEMBLY}/s04a.mp4"
ffmpeg -i "${CLIPS}/scene04b-autonomous-loop.mp4" -t 5 -c copy "${ASSEMBLY}/s04b.mp4"
ffmpeg -i "${CLIPS}/scene05-pick-your-ai.mp4" -t 7 -c copy "${ASSEMBLY}/s05.mp4"
ffmpeg -i "${CLIPS}/scene06-works-everywhere.mp4" -t 7 -c copy "${ASSEMBLY}/s06.mp4"
ffmpeg -i "${CLIPS}/scene07-quality-gates.mp4" -t 8 -c copy "${ASSEMBLY}/s07.mp4"
ffmpeg -i "${CLIPS}/scene08-audit-trail.mp4" -t 7 -c copy "${ASSEMBLY}/s08.mp4"
ffmpeg -i "${CLIPS}/scene09-self-maintenance.mp4" -t 8 -c copy "${ASSEMBLY}/s09.mp4"
ffmpeg -i "${CLIPS}/scene10-cta.mp4" -t 7 -c copy "${ASSEMBLY}/s10.mp4"
```

### Step 2: Concatenate Scene 4a + 4b (Seamless Cut)

```bash
# Scene 4 has two clips with seamless join
cat > "${ASSEMBLY}/scene4-list.txt" << 'EOF'
file 's04a.mp4'
file 's04b.mp4'
EOF

ffmpeg -f concat -safe 0 -i "${ASSEMBLY}/scene4-list.txt" \
  -c copy "${ASSEMBLY}/s04.mp4"
```

### Step 3: Apply Transitions with xfade Filter

```bash
# Build the full video with crossfade/wipe transitions between scenes.
# xfade filter: offset = cumulative_time - transition_duration
#
# Scene durations: s01=8, s02=5, s03=8, s04=10, s05=7, s06=7, s07=8, s08=7, s09=8, s10=7
# Transition types and durations from the table above.
#
# NOTE: Runway Gen4 Turbo produces silent video (no audio track).
# All audio comes from ElevenLabs narration + sound effects, mixed in later steps.

ASSEMBLY="docs/video/output/assembly"

ffmpeg \
  -i "${ASSEMBLY}/s01.mp4" \
  -i "${ASSEMBLY}/s02.mp4" \
  -i "${ASSEMBLY}/s03.mp4" \
  -i "${ASSEMBLY}/s04.mp4" \
  -i "${ASSEMBLY}/s05.mp4" \
  -i "${ASSEMBLY}/s06.mp4" \
  -i "${ASSEMBLY}/s07.mp4" \
  -i "${ASSEMBLY}/s08.mp4" \
  -i "${ASSEMBLY}/s09.mp4" \
  -i "${ASSEMBLY}/s10.mp4" \
  -filter_complex "
    [0:v][1:v]xfade=transition=wiperight:duration=0.7:offset=7.3[v01];
    [v01][2:v]xfade=transition=fade:duration=0.5:offset=11.8[v02];
    [v02][3:v]xfade=transition=slideleft:duration=0.4:offset=19.3[v03];
    [v03][4:v]xfade=transition=fade:duration=0.5:offset=28.9[v04];
    [v04][5:v]xfade=transition=fade:duration=0.5:offset=35.4[v05];
    [v05][6:v]xfade=transition=fade:duration=0.5:offset=41.9[v06];
    [v06][7:v]xfade=transition=fade:duration=0.5:offset=49.4[v07];
    [v07][8:v]xfade=transition=wiperight:duration=0.7:offset=55.9[v08];
    [v08][9:v]xfade=transition=wiperight:duration=0.7:offset=63.2[v09];
    [v09]fade=t=out:st=6.0:d=1.0[vout]
  " \
  -map "[vout]" \
  -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p \
  -r 24 \
  "${ASSEMBLY}/video-no-audio.mp4"
```

**Offset calculation notes** (cumulative duration minus transition overlap):
- s01->s02: offset = 8 - 0.7 = 7.3
- s02->s03: offset = 7.3 + 5 - 0.5 = 11.8
- s03->s04: offset = 11.8 + 8 - 0.4 = 19.4 (rounded to 19.3 for safety)
- s04->s05: offset = 19.4 + 10 - 0.5 = 28.9
- s05->s06: offset = 28.9 + 7 - 0.5 = 35.4
- s06->s07: offset = 35.4 + 7 - 0.5 = 41.9
- s07->s08: offset = 41.9 + 8 - 0.5 = 49.4
- s08->s09: offset = 49.4 + 7 - 0.7 = 55.7 (using 55.9 for slight padding)
- s09->s10: offset = 55.9 + 8 - 0.7 = 63.2
- Final fade-out at: 63.2 + 7 - 1.0 = 69.2 (st=6.0 relative to scene 10)

### Step 4: Mix Narration Audio

```bash
AUDIO="docs/video/output/audio"
ASSEMBLY="docs/video/output/assembly"

# Concatenate narration files with silence gaps matching scene timing
# Each narration starts at the beginning of its scene

ffmpeg \
  -i "${AUDIO}/narration-scene01.mp3" \
  -i "${AUDIO}/narration-scene02.mp3" \
  -i "${AUDIO}/narration-scene03.mp3" \
  -i "${AUDIO}/narration-scene04.mp3" \
  -i "${AUDIO}/narration-scene05.mp3" \
  -i "${AUDIO}/narration-scene06.mp3" \
  -i "${AUDIO}/narration-scene07.mp3" \
  -i "${AUDIO}/narration-scene08.mp3" \
  -i "${AUDIO}/narration-scene09.mp3" \
  -i "${AUDIO}/narration-scene10.mp3" \
  -filter_complex "
    [0:a]adelay=0|0[a0];
    [1:a]adelay=7300|7300[a1];
    [2:a]adelay=11800|11800[a2];
    [3:a]adelay=19300|19300[a3];
    [4:a]adelay=28900|28900[a4];
    [5:a]adelay=35400|35400[a5];
    [6:a]adelay=41900|41900[a6];
    [7:a]adelay=49400|49400[a7];
    [8:a]adelay=55900|55900[a8];
    [9:a]adelay=63200|63200[a9];
    [a0][a1][a2][a3][a4][a5][a6][a7][a8][a9]amix=inputs=10:duration=longest:normalize=0[aout]
  " \
  -map "[aout]" \
  -c:a aac -b:a 192k \
  "${ASSEMBLY}/narration-mixed.aac"
```

### Step 5: Mix Sound Effects Layer

Layer the ElevenLabs-generated sound effects at transition points and key moments:

```bash
SFX_DIR="docs/video/output/sfx"
ASSEMBLY="docs/video/output/assembly"

# Create ambient tech hum loop for full video duration (~70s)
# Loop the 10s ambient hum to cover the whole video at -18dB (very subtle)
ffmpeg -stream_loop 7 -i "${SFX_DIR}/ambient-tech-hum.mp3" \
  -t 70 -af "volume=0.15" \
  -c:a aac -b:a 128k \
  "${ASSEMBLY}/ambient-loop.aac"

# Mix sound effects at specific timestamps
# Transition swooshes at each scene boundary, key moments for signature sounds
ffmpeg \
  -i "${SFX_DIR}/transition-purple-wipe.mp3" \
  -i "${SFX_DIR}/transition-swoosh.mp3" \
  -i "${SFX_DIR}/logo-gong.mp3" \
  -i "${SFX_DIR}/achievement-chime.mp3" \
  -i "${SFX_DIR}/alert-tone.mp3" \
  -i "${SFX_DIR}/rewind-whoosh.mp3" \
  -i "${SFX_DIR}/confirmation-beep.mp3" \
  -i "${SFX_DIR}/ui-click.mp3" \
  -filter_complex "
    [0:a]adelay=7000|7000,volume=0.6[wipe1];
    [1:a]adelay=11500|11500,volume=0.5[swoosh1];
    [1:a]adelay=19000|19000,volume=0.5[swoosh2];
    [2:a]adelay=11800|11800,volume=0.7[gong1];
    [3:a]adelay=25000|25000,volume=0.7[achieve1];
    [1:a]adelay=28500|28500,volume=0.5[swoosh3];
    [1:a]adelay=35000|35000,volume=0.5[swoosh4];
    [1:a]adelay=41500|41500,volume=0.5[swoosh5];
    [6:a]adelay=42000|42000,volume=0.5[beep1];
    [4:a]adelay=44500|44500,volume=0.6[alert1];
    [6:a]adelay=47000|47000,volume=0.5[beep2];
    [1:a]adelay=49000|49000,volume=0.5[swoosh6];
    [5:a]adelay=52500|52500,volume=0.6[rewind1];
    [7:a]adelay=54000|54000,volume=0.5[click1];
    [0:a]adelay=55500|55500,volume=0.6[wipe2];
    [0:a]adelay=62800|62800,volume=0.6[wipe3];
    [2:a]adelay=63200|63200,volume=0.4[gong2];
    [wipe1][swoosh1][swoosh2][gong1][achieve1][swoosh3][swoosh4][swoosh5][beep1][alert1][beep2][swoosh6][rewind1][click1][wipe2][wipe3][gong2]amix=inputs=17:duration=longest:normalize=0[sfxout]
  " \
  -map "[sfxout]" \
  -c:a aac -b:a 192k \
  "${ASSEMBLY}/sfx-mixed.aac"
```

### Step 6: Final Audio Mix

Combine narration, sound effects, and ambient hum into one audio track:

```bash
ASSEMBLY="docs/video/output/assembly"

ffmpeg \
  -i "${ASSEMBLY}/narration-mixed.aac" \
  -i "${ASSEMBLY}/sfx-mixed.aac" \
  -i "${ASSEMBLY}/ambient-loop.aac" \
  -filter_complex "
    [0:a]volume=1.0[narration];
    [1:a]volume=0.5[sfx];
    [2:a]volume=0.12[ambient];
    [narration][sfx][ambient]amix=inputs=3:duration=first:normalize=0[aout]
  " \
  -map "[aout]" \
  -c:a aac -b:a 192k \
  "${ASSEMBLY}/audio-final.aac"
```

---

## Final Render

### Combine Video + Audio

```bash
ASSEMBLY="docs/video/output/assembly"
OUTPUT="docs/video/output"

# Final merge: silent video + fully mixed audio
ffmpeg \
  -i "${ASSEMBLY}/video-no-audio.mp4" \
  -i "${ASSEMBLY}/audio-final.aac" \
  -c:v copy \
  -c:a aac -b:a 192k \
  -shortest \
  "${OUTPUT}/eli5-final.mp4"
```

### Quality Check

```bash
# Verify output specs
ffprobe -v quiet -print_format json -show_format -show_streams \
  "${OUTPUT}/eli5-final.mp4" | jq '{
    duration: .format.duration,
    size_mb: (.format.size | tonumber / 1048576 | . * 100 | floor / 100),
    video: .streams[] | select(.codec_type=="video") | {
      codec: .codec_name,
      resolution: "\(.width)x\(.height)",
      fps: .r_frame_rate
    },
    audio: .streams[] | select(.codec_type=="audio") | {
      codec: .codec_name,
      sample_rate: .sample_rate,
      bitrate: .bit_rate
    }
  }'

# Expected output:
# duration: ~70-75 seconds
# resolution: 1280x720 (Runway Gen4 Turbo native)
# codec: h264 + aac
```

### Resolution Scaling (if needed)

If a different output resolution is desired (e.g., 1920x1080 for YouTube), add a scaling step before stitching:

```bash
# Scale all assembly clips to 1920x1080 before xfade (upscale from 1280x720)
for f in "${ASSEMBLY}"/s*.mp4; do
  ffmpeg -i "$f" -vf "scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:-1:-1:color=0x0F0F1A" \
    -c:v libx264 -preset fast -crf 18 \
    "${f%.mp4}-scaled.mp4"
done
```

### File Inventory (Final)

```
docs/video/output/
  clips/                          # Raw Runway Gen4 Turbo clips (11 files)
    scene01-the-pain.mp4
    scene02-the-question.mp4
    scene03-meet-tamma.mp4
    scene04a-autonomous-loop.mp4
    scene04b-autonomous-loop.mp4
    scene05-pick-your-ai.mp4
    scene06-works-everywhere.mp4
    scene07-quality-gates.mp4
    scene08-audit-trail.mp4
    scene09-self-maintenance.mp4
    scene10-cta.mp4
  audio/                          # ElevenLabs narration (10 files)
    narration-scene01.mp3
    narration-scene02.mp3
    ...
    narration-scene10.mp3
  sfx/                            # ElevenLabs sound effects (9 files)
    transition-swoosh.mp3
    transition-purple-wipe.mp3
    ambient-tech-hum.mp3
    achievement-chime.mp3
    logo-gong.mp3
    alert-tone.mp3
    rewind-whoosh.mp3
    ui-click.mp3
    confirmation-beep.mp3
  assembly/                       # Intermediate processing
    s01.mp4 ... s10.mp4           # Trimmed clips
    scene4-list.txt               # Concat list for Scene 4
    s04.mp4                       # Merged Scene 4
    video-no-audio.mp4            # Stitched video (silent)
    narration-mixed.aac           # Mixed narration track
    sfx-mixed.aac                 # Mixed sound effects track
    ambient-loop.aac              # Looped ambient hum
    audio-final.aac               # Final combined audio
  eli5-final.mp4                  # FINAL OUTPUT
```

### Cost Estimate

| Service | Items | Rate | Cost |
|---------|-------|------|------|
| Freepik Runway Gen4 Turbo | ~85s of video (11 clips) | $0.12/s | ~$10.20 |
| ElevenLabs TTS | 10 narration clips (~80s total) | per-character | ~$1.50 |
| ElevenLabs SFX | 9 sound effects | per-generation | ~$0.90 |
| **Total** | | | **~$12.60** |

### Production Timeline

| Phase | Task | Estimated Time |
|-------|------|---------------|
| 1 | Generate narration audio (10 ElevenLabs TTS calls) | 2 minutes |
| 2 | Generate sound effects (9 ElevenLabs SFX calls) | 2 minutes |
| 3 | Generate video clips (11 Freepik Runway calls + polling) | 15-30 minutes |
| 4 | Trim and scale clips | 5 minutes |
| 5 | Stitch with transitions | 2 minutes |
| 6 | Mix audio (narration + SFX + ambient) | 3 minutes |
| 7 | Final render and QC | 3 minutes |
| **Total** | | **~30-50 minutes** |

---

*Production plan generated for the Tamma ELI5 Explainer Video. All 10 scenes specified with frame descriptions, motion prompts, narration timing, emotional arcs, transitions, and sound design. Technical pipeline: Runway Gen4 Turbo (via Freepik) for video, ElevenLabs for narration and sound effects, ffmpeg for post-production assembly. Ready for execution.*
