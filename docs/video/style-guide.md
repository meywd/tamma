# Tamma Explainer Video - Visual Style Guide

## Brand Identity

**Name**: Tamma (Arabic: "it is done" / "it is complete")
**Tagline**: "Tam, it's Done"
**Core Promise**: Ship features 3x faster while eliminating 60% of repetitive development toil

## Color Palette

| Role | Color | Hex | Usage |
|------|-------|-----|-------|
| Primary Purple | Electric Purple | `#7B61FF` | Main brand color, accents, CTAs, glowing elements |
| Primary Green | Emerald | `#10b981` | Success states, completion, growth, positive actions |
| Dark Background | Deep Navy | `#0F0F1A` | Main backgrounds for all scenes |
| Mid Background | Slate | `#1A1A2E` | Card backgrounds, secondary panels |
| Light Text | White | `#F0F0F5` | Primary text, headings |
| Muted Text | Silver | `#9CA3AF` | Secondary text, captions |
| Accent Gold | Warm Gold | `#F59E0B` | Premium badges, highlights, the Arabic heritage element |
| Error Red | Coral Red | `#EF4444` | Pain points, problems, failures |
| Info Blue | Sky Blue | `#3B82F6` | Information, links, neutral states |

## Visual Style: "Luminous Tech Noir"

All images should follow this consistent aesthetic:

### Core Style Descriptors (include in every prompt)
- **Base style**: Clean, modern digital illustration with a dark, luminous aesthetic
- **Rendering**: Flat illustration with subtle gradients, NOT photorealistic, NOT 3D rendered
- **Lighting**: Soft glowing elements on dark backgrounds, neon-inspired accents
- **Mood**: Professional yet approachable, futuristic but grounded
- **Line work**: Clean geometric shapes, rounded corners, minimal line art
- **Color treatment**: Dark navy/black backgrounds (#0F0F1A) with purple (#7B61FF) and green (#10b981) glowing accents
- **Texture**: Subtle dot grid or circuit-board pattern in backgrounds at very low opacity
- **Characters**: Simplified, friendly human figures (no detailed faces), diverse skin tones, casual tech-worker clothing
- **Typography within images**: Use clean sans-serif for any text shown in scene (Geist, Inter, or similar)

### Aspect Ratio
- All images: **16:9** (1920x1080 or equivalent)
- This matches standard video resolution

### Consistent Elements Across All Scenes

1. **Subtle grid pattern** in the background (represents structured code/data)
2. **Glowing purple (#7B61FF) accent lines** connecting elements (represents the autonomous pipeline)
3. **Green (#10b981) checkmarks or pulses** for completion/success states
4. **Rounded rectangles** for UI elements and cards (8-12px radius feel)
5. **Soft drop shadows** with purple tint for floating elements
6. **No harsh edges** -- everything feels polished and fluid

### Scene Type Templates

#### "Problem" Scenes
- Dominant colors: Muted grays, dull blues, coral red (#EF4444) accents
- Mood: Slightly chaotic, cluttered, overwhelming
- Style: Elements slightly askew, overlapping, showing disorganization
- A sense of weight and friction

#### "Solution" Scenes
- Dominant colors: Deep navy background, purple (#7B61FF) and green (#10b981) glowing elements
- Mood: Clean, organized, flowing, automated
- Style: Elements aligned, connected by flowing lines, clear hierarchy
- A sense of lightness and speed

#### "Feature" Scenes
- Dominant colors: Navy background with one featured accent color
- Mood: Focused, clear, educational
- Style: Central element spotlighted, supporting elements dimmed around it
- Minimal clutter, maximum clarity

#### "CTA" Scenes
- Dominant colors: Rich purple gradient background, gold (#F59E0B) accents
- Mood: Inviting, exciting, premium
- Style: Centered composition, bold elements, clear visual hierarchy

### Arabic Heritage Element

The Arabic word "tamm" (finished/done) should appear as a design motif:
- Rendered in elegant Arabic calligraphy
- Used as a watermark, badge, or accent element
- Gold (#F59E0B) coloring when featured
- Represents quality, completion, and craftsmanship

### Do NOT Include
- Stock photo aesthetics
- Overly complex 3D renders
- Photorealistic humans
- Cluttered compositions with too many elements
- Bright white backgrounds (everything is dark-mode)
- Generic "AI brain" or "robot" imagery (Tamma is a tool, not a robot)
- Any text that could be misspelled (keep text minimal in images; narration carries the words)

## Typography (for text overlays in video editing)

| Role | Font | Weight | Size |
|------|------|--------|------|
| Scene title | Geist or Inter | Bold | 48-64px |
| Key stat | Geist or Inter | Extra Bold | 72-96px |
| Body text | Geist or Inter | Regular | 24-32px |
| Code snippets | JetBrains Mono | Regular | 20-28px |

## Transitions

| Transition | Usage | Duration |
|------------|-------|----------|
| **Smooth fade** | Default between most scenes | 0.5s |
| **Slide left** | Moving forward in a sequence/pipeline | 0.4s |
| **Zoom in** | Focusing on a detail or feature | 0.6s |
| **Wipe (purple glow)** | Major section change | 0.7s |
| **Cut** | Quick comparisons (before/after) | 0s |

## Music and Sound Direction (for video editor reference)

- **ELI5 version**: Light, upbeat electronic/lo-fi track, friendly and accessible
- **Deep dive version**: More sophisticated ambient electronic, builds energy through sections, quieter during technical explanation
- **Sound effects**: Subtle UI sounds for transitions (soft whoosh, gentle click), minimal and tasteful

## Image Generation Notes for Nano Banana

When generating images with Nano Banana, each prompt should:

1. Start with the style anchor: `"Digital illustration in a dark luminous tech-noir style."`
2. Specify the 16:9 aspect ratio
3. Include the color palette references explicitly: `"dark navy #0F0F1A background with glowing purple #7B61FF and emerald green #10b981 accents"`
4. Describe the scene composition (foreground, midground, background)
5. Specify the mood from the scene type template
6. End with negative prompts if needed: `"No photorealism, no bright white backgrounds, no 3D renders"`
