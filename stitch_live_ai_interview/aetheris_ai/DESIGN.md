# Design System Specification: High-End AI Interview Platform

## 1. Overview & Creative North Star
**Creative North Star: "The Digital Luminary"**

This design system rejects the "SaaS-in-a-box" aesthetic in favor of a high-end editorial experience. It is designed to feel like a premium concierge—intelligent, silent, and sophisticated. We move beyond standard grids by embracing **intentional asymmetry** and **tonal depth**. 

The system breaks the "template" look through:
*   **Atmospheric Layering:** Using translucency and backdrop blurs to create a sense of infinite Z-space.
*   **Typographic Authority:** Dramatic scale shifts between massive, airy headlines and hyper-legible utility text.
*   **Geometric Precision:** Utilizing sharp, mathematical shapes that evoke the logic of AI, softened by organic gradients.

## 2. Color Theory & Surface Logic
The palette is rooted in deep obsidian tones, punctuated by the "Pulse" of Indigo and the "Precision" of Rose.

### The "No-Line" Rule
**Strict Mandate:** Prohibit 1px solid borders for sectioning. Boundaries must be defined solely through background color shifts. For example, a `surface-container-low` section sitting on a `surface` background creates a sophisticated boundary that feels integrated, not "caged."

### Surface Hierarchy & Nesting
Treat the UI as a series of physical layers—stacked sheets of frosted glass.
*   **Base Layer (`surface` / #141313):** The canvas.
*   **Secondary Layer (`surface-container-low`):** For large content areas.
*   **Tertiary Layer (`surface-container-high`):** For interactive cards or "floating" modules.
*   **The Hero Gradient:** Use a linear transition from `primary` (#c0c1ff) to `primary-container` (#8083ff) at a 135° angle for primary CTAs to provide "visual soul."

### The "Glass & Gradient" Rule
Floating elements (modals, dropdowns, navigation) must utilize Glassmorphism:
*   **Fill:** `surface-variant` at 40% opacity.
*   **Effect:** `backdrop-filter: blur(12px)`.
*   **Edge:** Use the **Ghost Border** (see Elevation & Depth).

## 3. Typography
We utilize a dual-typeface strategy to balance "Tech-Forward" with "Editorial Elegance."

*   **Display & Headlines (Manrope):** High-character, geometric sans-serif used for impact. Use `display-lg` (3.5rem) for hero moments to create a "digital poster" feel.
*   **Body & UI (Inter):** The workhorse. Inter’s tall x-height ensures readability during high-stakes AI interview transcripts.
*   **Hierarchy Note:** Always maintain a minimum 2:1 size ratio between headlines and body text to ensure a signature, high-contrast look.

| Token | Size | Family | Usage |
| :--- | :--- | :--- | :--- |
| `display-lg` | 3.5rem | Manrope | Hero headers / Large data points |
| `headline-sm` | 1.5rem | Manrope | Section headers |
| `title-md` | 1.125rem | Inter | Sub-headers / Card titles |
| `body-md` | 0.875rem | Inter | Primary reading text |
| `label-sm` | 0.6875rem | Inter | Metadata / Technical specs |

## 4. Elevation & Depth
Depth is achieved through **Tonal Layering**, not shadows.

*   **The Layering Principle:** To lift an element, shift the token value rather than adding a shadow. Place a `surface-container-highest` card on a `surface-container-low` background. This creates a soft, natural lift.
*   **Ambient Shadows:** If an element must "float" (e.g., a candidate's profile video), use a highly diffused shadow: `box-shadow: 0 20px 50px rgba(0, 0, 0, 0.4)`. The shadow must never be pure black; it should feel like a dark tint of the background.
*   **The "Ghost Border" Fallback:** Where a divider is required for accessibility, use the `outline-variant` token at **15% opacity**. High-contrast, 100% opaque borders are strictly forbidden.

## 5. Components & Primitive Logic

### Buttons: The Geometric Interactive
*   **Primary:** A gradient fill (`primary` to `primary-container`). Corner radius: `md` (0.375rem). No border.
*   **Secondary:** Ghost style. Transparent background with a `Ghost Border` and `on-surface` text.
*   **Rose Variant:** Use `secondary` (#ffb2b7) exclusively for "End Interview" or high-caution actions.

### Input Fields: The Immersive Entry
*   **Styling:** No bottom line or full box. Use a subtle `surface-container-highest` background with a `sm` (0.125rem) radius. 
*   **Focus State:** A 1px glow using the `primary` token at 30% opacity.

### Cards & Lists: The Negative Space Rule
*   **Cards:** Forbid the use of divider lines. Use vertical white space—specifically the `8` (2.75rem) or `10` (3.5rem) spacing tokens—to separate content modules.
*   **Lists:** Separate items using a subtle shift from `surface-container-low` to `surface-container-lowest` on alternate rows.

### Signature Component: The AI Pulse (Unique to this App)
A geometric shape (from the HeroGeometric set) placed behind the candidate's video feed, using a slow, 8-second breathing animation with a `primary` to `secondary` gradient blur. This signals the AI is "listening" without using clichéd waveform icons.

## 6. Do’s and Don’ts

### Do:
*   **Do** use asymmetrical layouts (e.g., a left-aligned headline with a right-aligned video feed) to create a premium, custom feel.
*   **Do** use the `20` (7rem) spacing token for top-level section padding to let the design breathe.
*   **Do** use `on-surface-variant` for secondary text to maintain low-contrast sophistication.

### Don't:
*   **Don't** use 1px solid lines to separate content. 
*   **Don't** use standard "Blue" for links. Use the `primary` (#c0c1ff) or `secondary` (#ffb2b7) tokens.
*   **Don't** use sharp corners. Stick to the `md` (0.375rem) or `xl` (0.75rem) tokens to maintain the "Soft Minimalism" feel.
*   **Don't** use pure white (#FFFFFF) for text. Always use `on-surface` (#e5e2e1) to reduce eye strain in dark mode.