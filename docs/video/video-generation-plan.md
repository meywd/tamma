# Video Generation Plan

## APIs Available

### Freepik (Kling v2)
- **Endpoint**: `POST https://api.freepik.com/v1/ai/image-to-video/kling-v2`
- **Key**: `FPSXc00dd4679d29141091b6be3f512fe0d1`
- **Duration**: "5" or "10" seconds
- **Concurrency**: max 3 simultaneous tasks
- **Processing time**: ~2.5 min per clip
- **Output**: 1284x716, H.264, 24fps
- **Limitation**: Single image input only (no start+end frame)
- **Poll**: `GET /v1/ai/image-to-video/kling-v2/{task_id}`

### Veo 3.1 (Google Gemini API)
- **Endpoint**: `POST https://generativelanguage.googleapis.com/v1beta/models/veo-3.1-generate-preview:predictLongRunning`
- **Key**: Gemini API key
- **Duration**: 4, 6, or 8 seconds
- **Resolution**: 720p (4/6/8s) or 1080p (8s only)
- **Processing time**: ~30-90 sec (720p), ~60-120 sec (1080p)
- **Output**: H.264 MP4, 24fps, AAC audio auto-generated
- **Supports start+end frame** (first frame + last frame interpolation)
- **Poll**: `GET /v1beta/{operation_name}`

## Recommended Approach: Veo 3.1

Veo supports first+last frame — this is critical for using our A→B keyframe pairs. Freepik Kling only takes a single image.

## Image Asset Summary

### ELI5 Video (10 scenes, ~75 seconds)

| Scene | Duration | Images | Start Frame | End Frame | Motion Prompt |
|-------|----------|--------|-------------|-----------|---------------|
| 1 | 8s | A+B | eli5/01-the-pain.png | eli5/extra/01B-the-pain.png | Slow zoom in on overwhelmed developer, screens flicker with error messages, slight parallax on floating windows |
| 2 | 5s | A only | eli5/02-the-question.png | — | Purple question mark pulses and glows, faint icons slowly fade in around it, subtle rotation |
| 3 | 8s | A+B | eli5/03-meet-tamma.png | eli5/extra/03B-meet-tamma.png | Logo emblem materializes with radiating light beams expanding outward, text fades in below |
| 4a | 5s | A→B | eli5/04-autonomous-loop.png | eli5/extra/04B-autonomous-loop.png | Camera tracks along pipeline left to right, pulse lights travel along connections |
| 4b | 5s | B→C | eli5/extra/04B-autonomous-loop.png | eli5/extra/04C-autonomous-loop.png | Continue tracking right, merge node activates with green burst, confetti particles |
| 5 | 7s | A+B | eli5/05-pick-your-ai.png | eli5/extra/05B-pick-your-ai.png | Central hub pulses, connection lines illuminate one by one to each provider circle |
| 6 | 7s | A+B | eli5/06-works-everywhere.png | eli5/extra/06B-works-everywhere.png | Platform cards float and rotate slightly, green connection lines pulse toward center |
| 7 | 8s | A+B | eli5/07-quality-gates.png | eli5/extra/07B-quality-gates.png | Code block slides along conveyor belt, passes through each shield gate, checkmarks appear |
| 8 | 7s | A+B | eli5/08-audit-trail.png | eli5/extra/08B-audit-trail.png | Timeline scrolls upward, event cards fade in one by one, rewind icon glows |
| 9 | 8s | A+B | eli5/09-self-maintenance.png | eli5/extra/09B-self-maintenance.png | Ouroboros loop rotates slowly, green pulses travel along the circular flow, center logo breathes |
| 10 | 7s | A+B | eli5/10-cta.png | eli5/extra/10B-cta.png | Logo scales up with golden glow, URL text fades in, GitHub icon appears, particles converge |

### Deep Dive Video (18 scenes, ~3:46)

| Scene | Duration | Images | Start Frame | End Frame | Motion Prompt |
|-------|----------|--------|-------------|-----------|---------------|
| 1a | 5s | A→B | deep-dive/01-developer-burnout.png | deep-dive/extra/01B-developer-burnout.png | Camera zooms into the 60% bar chart, error icons multiply and flash |
| 1b | 5s | B→C | deep-dive/extra/01B-developer-burnout.png | deep-dive/extra/01C-developer-burnout.png | Pull back to reveal the full weight of wasted time crushing the developer |
| 2a | 6s | A→B | deep-dive/02-autocomplete-not-autonomy.png | deep-dive/extra/02B-autocomplete.png | Camera pans from small autocomplete cursor across the gap |
| 2b | 6s | B→C | deep-dive/extra/02B-autocomplete.png | deep-dive/extra/02C-autocomplete.png | Full pipeline lights up on the right side, showing the scale difference |
| 3a | 5s | A→B | deep-dive/03-tamma-intro.png | deep-dive/extra/03B-fear-of-autonomy.png | Transition from fear to trust, nightmare scenarios dissolve |
| 3b | 5s | B→C | deep-dive/extra/03B-fear-of-autonomy.png | deep-dive/extra/03C-fear-of-autonomy.png | Shield materializes with trust requirements, mood shifts from dark to confident |
| 4a | 4s | A→B | deep-dive/04-fourteen-step-pipeline.png | deep-dive/extra/04B-tamma-it-is-done.png | Logo dissolves into particles that reform as pipeline overview |
| 4b | 4s | B→C | deep-dive/extra/04B-tamma-it-is-done.png | deep-dive/extra/04C-tamma-it-is-done.png | Pipeline completes, merged PRs stack up with success badges |
| 5a | 5s | A→B | deep-dive/05-multi-provider-ai.png | deep-dive/extra/05B-end-to-end-autonomy.png | Upper arc of pipeline steps illuminate sequentially |
| 5b | 5s | B→C→D | deep-dive/extra/05B-end-to-end-autonomy.png | deep-dive/extra/05D-end-to-end-autonomy.png | Lower arc completes, full cycle glows |
| 6a | 5s | A→B | deep-dive/06-multi-platform-git.png | deep-dive/extra/06B-you-stay-in-control.png | Approval gate opens with human interaction |
| 6b | 5s | B→C | deep-dive/extra/06B-you-stay-in-control.png | deep-dive/extra/06C-you-stay-in-control.png | Gate opens, code flows through to deployment |
| 7a | 5s | A→B | deep-dive/07-quality-gates.png | deep-dive/extra/07B-any-ai-your-choice.png | Provider hub activates, routing lines illuminate |
| 7b | 5s | B→C | deep-dive/extra/07B-any-ai-your-choice.png | deep-dive/extra/07C-any-ai-your-choice.png | Circuit breaker triggers, fallback provider activates smoothly |
| 8a | 5s | A→B | deep-dive/08-event-sourcing.png | deep-dive/extra/08B-every-git-platform.png | Platform cards arrange across the frame |
| 8b | 5s | B→C | deep-dive/extra/08B-every-git-platform.png | deep-dive/extra/08C-every-git-platform.png | Config panel zooms in showing platform selection |
| 9a | 5s | A→B | deep-dive/09-elsa-workflows.png | deep-dive/extra/09B-quality-gates.png | Quality gate shields activate, retry loop visualized |
| 9b | 5s | B→C | deep-dive/extra/09B-quality-gates.png | deep-dive/extra/09C-quality-gates.png | Escalation to human when AI can't fix, handoff animation |
| 10a | 4s | A→B | deep-dive/10-config-driven-routing.png | deep-dive/extra/10B-time-travel-debugging.png | Timeline zooms into event detail |
| 10b | 4s | B→C→D | deep-dive/extra/10B-time-travel-debugging.png | deep-dive/extra/10D-time-travel-debugging.png | State snapshot reveals, compliance badges appear |
| 11 | 8s | A→B→C | deep-dive/11-self-maintenance.png | deep-dive/extra/11C-self-maintenance.png | ELSA editor shows workflow, dual-stack bridge visualized |
| 12 | 8s | A→B→C | deep-dive/12-sarahs-story-1.png | deep-dive/extra/12C-sarahs-story-1.png | Config routing diagram animates, diagnostics dashboard appears |
| 13 | 8s | A→B→C | deep-dive/13-sarahs-story-2.png | deep-dive/extra/13C-sarahs-story-2.png | Self-maintenance ouroboros rotates, progress bar fills |
| 14a | 4s | A→B | deep-dive/14-mentorship-model.png | deep-dive/extra/14B-sarahs-story.png | Sarah's story begins, issue appears in chat |
| 14b | 4s | B→C | deep-dive/extra/14B-sarahs-story.png | deep-dive/extra/14C-sarahs-story.png | TDD code editor shows tests passing |
| 14c | 4s | C→D | deep-dive/extra/14C-sarahs-story.png | deep-dive/extra/14D-sarahs-story.png | Time comparison reveals 75% savings |
| 15 | 8s | A→B→C | deep-dive/15-tech-stack.png | deep-dive/extra/15C-ai-learns-feedback.png | Learning pipeline flows, improvement metrics rise |
| 16a | 4s | A→B | deep-dive/16-current-status.png | deep-dive/extra/16B-built-for-production.png | Tech stack layers build up from bottom |
| 16b | 4s | B→C→D | deep-dive/extra/16B-built-for-production.png | deep-dive/extra/16D-built-for-production.png | Monorepo grid fills, deployment architecture deploys |
| 17a | 4s | A→B | deep-dive/17-vision.png | deep-dive/extra/17B-where-we-are.png | Epic grid populates with progress |
| 17b | 4s | B→C→D | deep-dive/extra/17B-where-we-are.png | deep-dive/extra/17D-where-we-are.png | Dashboard shows live metrics, roadmap timeline extends into future |
| 18 | 8s | A→B→C | deep-dive/18-cta.png | deep-dive/extra/18C-join-the-movement.png | Community network forms, CTA buttons glow, grand finale |

## Post-Production Pipeline

1. **Generate clips**: Veo 3.1 (start+end frame) or Freepik Kling (single frame)
2. **Stitch clips**: ffmpeg concat with xfade crossfade transitions (0.5s between scenes)
3. **Add narration**: ElevenLabs TTS, synced to scene timing per script
4. **Final render**: ffmpeg merges video + narration audio track
5. **Output**: Two MP4 files — eli5.mp4 (~80s) and deep-dive.mp4 (~3:46)

## Narration Voice

- **Provider**: ElevenLabs
- **Style**: Professional, warm, confident male voice
- **No background music** (per user request)
- **Subtle sound effects**: transition swooshes only (via ffmpeg)
