# DESIGN SYSTEM and UX SPECIFICATION — MECHWARE (Industry 4.0)

> The visual quality of the product is judged in the first 3 seconds. This file defines
> exactly how the MECHWARE platform must look, move and read. It merges the industrial depth
> of Siemens / McMaster-Carr with the sleek high-tech elegance of Apple Store.
> Brand assets are located at `assets/brand/` (`logo.svg`, `logo.png`, `hero-bg.png`).

---

## 1. Brand Personality

- **High-Tech & Futuristic** (Industry 4.0 cybernetic motifs, subtle circuit traces, clean glass elements).
- **Industrial Dependability** (rugged engineering precision, clear technical specifications, CAD/datasheet badges).
- **Fast & Responsive** (snappy search, instant filter response, 1-click RFQ & checkout).
- **Authoritative & Trustworthy** (proudly highlighting "Made in Bangladesh Machinery", ISO/CE compliance, client logos like Square, Singer, Hisense).

---

## 2. Design Tokens & MECHWARE Color Palette

All tokens live in `packages/ui/tokens` and are exposed as CSS variables + a Tailwind preset.

### 2.1 Color — MECHWARE Semantic Tokens

| Token | Light Mode | Dark Mode (Default) | Use |
|---|---|---|---|
| `--bg` | #F8FAFC | #060B18 | Main Page Background (Dark Midnight Navy) |
| `--bg-subtle` | #F1F5F9 | #0A1128 | Card surface, sidebar, section alternate bands |
| `--bg-muted` | #E2E8F0 | #101B3B | Input fields, technical table header, code blocks |
| `--surface` | #FFFFFF | #0D1635 | Floating panels, dropdowns, modal windows |
| `--border` | #CBD5E1 | #1B2B55 | Card borders, circuit divider lines |
| `--border-strong` | #94A3B8 | #2A4078 | Input focus states, active technical containers |
| `--text` | #0F172A | #F8FAFC | Primary headings and prominent titles |
| `--text-muted` | #475569 | #94A3B8 | Technical specs, descriptions, breadcrumbs |
| `--text-subtle` | #64748B | #64748B | SKU numbers, timestamps, pinouts |
| `--primary` | #0A1128 | #F8FAFC | Primary buttons, headers, high-contrast actions |
| `--primary-fg` | #FFFFFF | #060B18 | Text on primary |
| `--accent` (Mech Teal) | #0093A2 | #00E5FF | **Primary CTA Glow, Add to Cart, Request Quote, Live Circuit Traces** |
| `--accent-fg` | #FFFFFF | #060B18 | Text on accent |
| `--accent-hover` | #007A87 | #38EFFF | Hover state for CTAs |
| `--brand-blue` | #0F3E7D | #1A5CB0 | Deep Industrial Navy from the MW Monogram |
| `--deal` / `--fault` | #DC2626 | #F87171 | System fault, discount badge, critical alarm |
| `--success` / `--online` | #10B981 | #34D399 | Controller online, in-stock, verified partner |
| `--warning` / `--pending` | #F59E0B | #FBBF24 | Low stock alert, calibration due |
| `--focus-ring` | #00A8B5 | #00E5FF | 2px electric cyan ring with 2px offset |

Generate full 50–950 scales for primary, accent and neutrals for illustrations and charts.
Chart palette: 6 colors with tested contrast and colour-blind-safe order.

**Contrast rule**: every text/background pair must pass WCAG AA (4.5:1 body, 3:1 large text and UI).

### 2.2 Typography

- **UI / body**: Inter (variable). **Headings / display**: Plus Jakarta Sans.
  **Bangla**: Hind Siliguri (fallback Noto Sans Bengali). Loaded with `next/font`, `display: swap`, subset.
- Numbers in prices use `font-variant-numeric: tabular-nums`.

| Token | Size / line-height | Weight | Use |
|---|---|---|---|
| `display` | 56/64 (mobile 36/44) | 800 | Hero headline |
| `h1` | 40/48 (mobile 30/38) | 700 | Page title |
| `h2` | 32/40 (mobile 24/32) | 700 | Section title |
| `h3` | 24/32 (mobile 20/28) | 600 | Card group title |
| `h4` | 20/28 | 600 | Sub-section |
| `body-lg` | 18/28 | 400 | Lead paragraphs |
| `body` | 16/24 | 400 | Default |
| `body-sm` | 14/20 | 400 | Card meta, tables |
| `caption` | 12/16 | 500 | Labels, badges |
| `price-lg` | 32/40 | 700 | PDP price |
| `price` | 18/24 | 700 | Card price |

Letter-spacing: headings −0.02em, captions/overlines +0.04em uppercase.
Max line length for reading text: 70ch.

### 2.3 Spacing, layout and grid
- Spacing scale (px): 0, 2, 4, 8, 12, 16, 20, 24, 32, 40, 48, 64, 80, 96, 128.
- Breakpoints: `sm 640`, `md 768`, `lg 1024`, `xl 1280`, `2xl 1536`.
- Container: max-width 1440px, padding 16px (mobile) / 24px (tablet) / 32px (desktop).
- Grid: 4 columns mobile, 8 tablet, 12 desktop; gutter 16 / 20 / 24.
- Section vertical rhythm: 48px mobile, 80px desktop between homepage sections.
- Product grid columns: 2 (mobile), 3 (md), 4 (lg), 5 (2xl).

### 2.4 Radius, borders, shadows, z-index
- Radius: `xs 4`, `sm 6`, `md 10` (inputs, buttons), `lg 14` (cards), `xl 20` (modals, hero), `full`.
- Borders: 1px `--border`; inputs 1px `--border-strong`.
- Shadows (soft, layered, low opacity):
  - `xs`: 0 1px 2px rgba(15,23,42,.05)
  - `sm`: 0 1px 3px rgba(15,23,42,.08), 0 1px 2px rgba(15,23,42,.04)
  - `md`: 0 4px 12px rgba(15,23,42,.08)
  - `lg`: 0 12px 32px rgba(15,23,42,.12)
  - `xl`: 0 24px 64px rgba(15,23,42,.16)
- Z-index scale: base 0, sticky 100, header 200, dropdown 300, drawer 400, modal 500, toast 600, tooltip 700.

### 2.5 Motion
- Durations: `fast 120ms` (hover, press), `base 200ms` (dropdowns, toggles),
  `slow 320ms` (drawers, modals, page transitions).
- Easing: standard `cubic-bezier(0.2, 0.8, 0.2, 1)`; exit `cubic-bezier(0.4, 0, 1, 1)`.
- Press feedback: scale 0.98. Card hover: lift 2px + shadow `md` + second image crossfade.
- Add-to-cart: button shows check icon for 1.2s, cart badge bounces, mini-cart drawer opens.
- Respect `prefers-reduced-motion`: disable parallax, carousels autoplay and large transforms.
- Never animate layout properties that cause CLS. Use transform/opacity only.

### 2.6 Iconography and imagery
- Icons: Lucide, 1.5px stroke, sizes 16/20/24, always paired with text or `aria-label`.
- Product images: square 1:1, pure white or `#F8FAFC` background, product fills ~80% of the
  frame, minimum 1200×1200 source, consistent across the grid.
- Lifestyle / hero images: 16:9 desktop, 4:5 mobile (art-directed with `<picture>`).
- Every image has `width`/`height`, a blur placeholder and meaningful `alt` text.
- Empty states and errors use a consistent custom illustration style (line art, accent colour highlights).

---

## 3. Component Specifications

Every component: all variants, sizes and states (default, hover, focus-visible, active,
disabled, loading, error), keyboard support, ARIA roles, RTL-safe, dark mode, and a story on
the `/design-system` showcase page.

| Component | Requirements |
|---|---|
| **Button** | Variants: `accent` (buy CTAs), `primary`, `secondary`, `outline`, `ghost`, `destructive`, `link`. Sizes: sm 32px, md 40px, lg 48px, xl 56px (mobile sticky CTA). Loading shows spinner and keeps width. Icon-only variant requires `aria-label`. |
| **Input / Textarea / Select / Combobox** | Label above (never placeholder-only), helper text, error text with icon, character counter, prefix/suffix (currency, +880). Height 44px on mobile (touch target). |
| **Phone input** | Country code selector, BD format validation (`01XXXXXXXXX`). |
| **Checkbox / Radio / Switch / Radio cards** | Radio cards used for delivery and payment options. |
| **Quantity stepper** | −/+ buttons 40px, direct input, min/max with disabled states, debounced API update. |
| **Price** | Props: amount, compareAt, currency, size. Shows currency symbol per locale (৳ / $), strikethrough compare price, discount badge "−23%". Tabular numerals. |
| **Rating** | Stars with half-star support, average + count ("4.6 · 1,284 ratings"), accessible text. |
| **Badge** | `deal`, `new`, `bestseller`, `authentic`, `low-stock`, `free-delivery`, `ai`. |
| **Product card** | See 3.1. |
| **Card, Tabs, Accordion, Tooltip, Popover, Dropdown menu, Modal (Dialog), Drawer (Sheet), Toast, Alert banner, Breadcrumb, Pagination, Skeleton, Avatar, Progress, Stepper, Carousel, Countdown timer, Image gallery, Data table, Empty state, Error state, Search bar with autocomplete, Mega menu, Rich text renderer, File uploader (drag & drop with previews and progress).** | Built on Radix primitives via shadcn/ui, restyled to these tokens. |

### 3.1 Product card (the most repeated element — must be perfect)
- Square image with hover crossfade to second image (desktop), wishlist heart top-right
  (animated fill), up to 2 badges top-left.
- Brand (caption, muted) → title (2 lines max, ellipsis) → rating → price block
  (price, compare-at, discount %) → delivery hint ("Free delivery", "Get it tomorrow").
- Quick "Add to cart" button: appears on hover desktop, always visible as a small icon button on mobile. Variant products open a quick-view sheet to choose options.
- Whole card is one link (accessible), but nested buttons work independently.
- Skeleton version with identical dimensions (no layout shift).
- Sizes: `default` (grid), `compact` (carousels), `horizontal` (cart recommendations, search dropdown).

---

## 4. Page Specifications

### 4.1 Global header (sticky; condenses on scroll)
- Top utility bar (desktop): delivery location selector ("Deliver to Dhaka 1207"),
  language switch (EN/বাং), currency, help, "Sell on ShopVerse".
- Main bar: logo · category button (opens mega menu) · **large search bar (center, flex-grow)**
  with category scope select · account menu (Hello, Nadia / Sign in) · Orders · Wishlist (count) · Cart (count + total on desktop).
- Search autocomplete (opens within 100ms of typing, debounced 150ms): recent searches,
  trending searches, suggested queries with highlighted match, top 4 products with
  thumbnail + price, matching categories/brands. Full keyboard navigation (↑ ↓ Enter Esc).
- Mega menu: 3-level category tree with images for featured sub-categories and a promo tile.
- Mobile: logo + search icon expands to full-screen search; hamburger opens drawer with
  categories; **bottom navigation bar**: Home, Categories, Search, Cart, Account.

### 4.2 Homepage (sections in this order; all content from CMS)
1. Hero carousel: 3–5 slides, art-directed images, headline + subline + CTA, autoplay 6s
   paused on hover/focus, dots + arrows, swipe on mobile. First slide image is the LCP
   element: preloaded, `priority`, no layout shift.
2. Value strip: Free delivery over X · Cash on delivery · 7-day easy returns · 100% authentic · Secure payment.
3. Shop by category: circular or rounded-square tiles with images (horizontal scroll on mobile).
4. Deals of the Day: live countdown, horizontal product carousel with claimed-% progress bars.
5. Personalized "Recommended for you" / for guests "Trending now".
6. Two-up and three-up promotional banners (campaigns, brand stores).
7. Best sellers by category (tabs).
8. Top brands strip (logos, grayscale → color on hover).
9. New arrivals.
10. "Continue shopping" / recently viewed (only if history exists).
11. App download / newsletter block with clear incentive ("Get 10% off your first order").
12. Rich footer: 4–5 link columns, payment method logos, courier partners, social, app badges,
    trust certifications, language/currency, legal links, copyright.

### 4.3 Category / search results page (PLP)
- Breadcrumb · H1 + result count · short SEO intro (collapsible) · sub-category chips.
- Left sidebar filters (desktop) / bottom-sheet filter drawer (mobile): price range slider
  with inputs, brand (searchable checklist with counts), rating (4★ & up), dynamic category
  attributes (RAM, size, color swatches…), availability, delivery speed, seller, discount %.
- Active filter chips with "Clear all". Filters sync to URL. Counts update live.
- Toolbar: sort (Relevance, Best selling, Price ↑, Price ↓, Newest, Top rated), grid/list toggle.
- Sponsored slot placeholders reserved (Phase 2).
- Pagination with "Load more" + page numbers (SEO-friendly links).
- Zero-results state: spelling suggestion ("Did you mean…"), popular categories, trending products.
- Category FAQ block at the bottom (FAQPage JSON-LD).

### 4.4 Product detail page (PDP)
Desktop 3-column layout: gallery (≈45%) · info (≈35%) · sticky buy box (≈20%).
- **Gallery**: thumbnail rail, main image with hover zoom (desktop) / pinch-zoom (mobile),
  fullscreen lightbox, video support, variant-specific images.
- **Info**: brand link, title (h1), rating + review count (anchor to reviews) + Q&A count,
  "#1 Best seller in X" badge, price block, savings, VAT note, EMI hint, variant selectors
  (color swatches with image preview on hover, size buttons, disabled out-of-stock options
  with "Notify me"), size guide modal, key highlights (5 bullets), offers list
  ("10% off with bKash", coupon chips with one-click apply).
- **Buy box**: price, delivery estimate with location change, stock status ("In stock",
  "Only 3 left"), quantity, **Add to cart (accent)**, **Buy now (primary)**, wishlist,
  share, seller card (name, rating, "Visit store"), return policy, warranty, secure transaction note.
- **Below the fold** (tabs on desktop, accordions on mobile): description (rich media),
  specifications table (grouped), what's in the box, shipping & returns.
- Frequently bought together (bundle with combined price and one-click add).
- **Reviews**: AI summary ("Customers say…") + pros/cons chips, rating histogram (clickable
  filters), photo reviews gallery, sort/filter, helpful votes, verified badges, seller replies.
- Questions & answers with search.
- Compare with similar items (table of 4), Customers also viewed, More from this seller, Recently viewed.
- **Mobile**: sticky bottom bar with price + Add to cart, appearing after the main CTA scrolls out of view.

### 4.5 Cart
- Mini-cart drawer on add-to-cart (items, subtotal, free-shipping progress bar, View cart / Checkout).
- Cart page: items grouped by seller, image, title, variant, price, quantity stepper, remove,
  save for later, delivery estimate per group; order summary sticky on desktop (subtotal,
  discounts, shipping estimate, total, coupon field with clear success/error messaging,
  "Proceed to checkout"); price/stock change notices; recommendations below; empty cart state
  with popular products.

### 4.6 Checkout (distraction-free — no header navigation, no footer links except help)
- Steps on one page with accordion sections (mobile) / two-column with sticky summary (desktop):
  1. Contact (email or phone; sign-in prompt; continue as guest).
  2. Shipping address (saved addresses as radio cards; new address form with division →
     district → area cascading selects; address validation).
  3. Delivery option (radio cards with price and date).
  4. Payment (radio cards with logos: Card, bKash, Nagad, SSLCommerz, COD with fee note).
  5. Review and place order (terms checkbox, total in large type, "Place order" accent button, trust badges).
- Inline validation on blur, never on every keystroke. Autofill attributes set correctly
  (`autocomplete="shipping street-address"` etc.). Errors summarised at top and focused.
- Payment processing state with clear messaging; prevent double-submit.
- Order confirmation: success animation, order number, delivery estimate, items, "Track
  order", "Continue shopping", create-account prompt for guests (1 field: password).

### 4.7 Customer account
Sidebar navigation (desktop) / list (mobile): Overview, Orders, Returns, Addresses, Payment
methods, Wishlist, Reviews, Notifications, Profile & security (password, 2FA, sessions,
delete account), Language & preferences.
Order detail: visual status timeline (stepper), shipment tracking events, invoice PDF
download, buy again, cancel / return / review actions per item, help link with order context.

### 4.8 Other pages
Seller store page (banner, logo, rating, followers, categories, products, reviews),
brand page, help center (search, categories, articles, contact), track order (by order
number + phone, no login), 404 (search + popular categories), 500 (friendly, retry), maintenance page.

### 4.9 Console (Admin + Seller center)
- Layout: collapsible left sidebar with icons + labels, top bar with global search (⌘K
  command palette), notifications, account; breadcrumb + page title + primary action on every page.
- Dashboard: KPI cards with sparkline and % change vs previous period, revenue chart,
  orders by status, top products, low stock, recent orders, date-range picker.
- Data tables: server-side pagination, sorting, filters panel, saved views, column
  visibility, row selection + bulk actions, CSV export, sticky header, empty state with CTA.
- Forms: sectioned cards, sticky save bar when dirty, unsaved-changes warning, inline validation.
- Product editor: sections (basic info, media drag-and-drop with reorder and alt text,
  variants matrix generator, pricing, inventory per warehouse, shipping, SEO preview,
  AI listing assistant button), live storefront preview.
- Order detail: timeline, items per seller, payment info, refund modal, customer card, internal notes.

---

## 5. UX and Conversion Principles

1. **Delivery promise everywhere**: product card, PDP, cart, checkout show a concrete date.
2. **Honest urgency only**: real stock counts and real deal end times. No fake timers, no fake "X people are viewing".
3. **Social proof**: ratings, review counts, "1.2k sold", verified badges, photo reviews.
4. **Trust**: authentic badges, clear return policy, secure payment icons, visible support contact, seller identity.
5. **Friction removal**: guest checkout, saved addresses, autofill, one-tap reorder, no forced sign-up.
6. **Speed is a feature**: optimistic updates for cart/wishlist, prefetch on hover, skeletons, instant search.
7. **Recovery**: abandoned-cart email, back-in-stock and price-drop alerts, saved for later.
8. **Mobile thumb zone**: primary actions in the bottom third on mobile; touch targets ≥ 44×44px.
9. **No dark patterns**: no pre-checked add-ons, no hidden fees, easy unsubscribe and account deletion.

---

## 6. Copywriting Voice

- Clear, short, confident, helpful. Speak to "you". Lead with the benefit.
- Buttons are verbs: "Add to cart", "Place order", "Track package" — never "Submit" or "OK".
- Errors say what happened and how to fix it: "That card was declined. Try another card or choose Cash on Delivery."
- Empty states invite action: "Your wishlist is empty. Tap ♡ on anything you love to save it here."
- Numbers are formatted by locale: ৳1,25,000 (BD lakh grouping in Bangla/BDT), $1,250.00.
- Dates: "Thu, 31 Oct". Relative times for recent activity: "2 hours ago".
- Every string is in i18n files; Bangla copy is written naturally, not machine-literal.

---

## 7. Accessibility Checklist (WCAG 2.2 AA)
- Semantic landmarks (header, nav, main, footer), one h1 per page, logical heading order.
- Everything works with keyboard only; visible focus ring on every interactive element; skip-to-content link.
- Dialogs trap focus and restore it on close; Esc closes.
- Form fields have labels; errors linked with `aria-describedby`; live regions for cart updates and toasts.
- Color is never the only signal (e.g., stock status has icon + text).
- Carousels have pause controls; motion respects reduced-motion.
- Images have meaningful alt; decorative images `alt=""`.
- Target size ≥ 24×24 (AA), aim for 44×44 on mobile.

---

## 8. Visual QA Checklist (run before every phase sign-off)
- [ ] Spacing uses only scale values; alignment on the grid; consistent card heights.
- [ ] Typography uses only scale tokens; no orphaned single words in headings where avoidable.
- [ ] Images consistent ratio and background; no stretched or blurry images.
- [ ] All states present: hover, focus, active, disabled, loading, empty, error.
- [ ] No layout shift when data loads (skeletons match final size).
- [ ] Looks intentional at 360, 768, 1024, 1280, 1920 px and in dark mode.
- [ ] Bangla text does not overflow or clip (Bangla is ~20% wider).
- [ ] No console errors; no hydration warnings.
