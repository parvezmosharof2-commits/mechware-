# MechWare company facts (for quote / invoice / receipt PDFs)

Source: the owner's own pads in `D:\MECHWARE\COMPANY DOCUMENTS` (quotation pad, invoice pad), read 2026-11-20.

## Letterhead
- Name: MECHWARE (written "MechWare" in body text)
- Tagline: "YOUR GATEWAY TO INDUSTRY 4.0"; footer line "MECHWARE · TECHNOLOGY FOR SMARTER SYSTEMS"
- Office: Sha-23/E (6th Floor) — **area/city/post code missing (owner)**
- Phone: +88 01748-305931
- Email: parvez.mechware@gmail.com
- Signatory: Md. Parvez Mosharaf, Managing Director (the pad also uses "Sr. Automation Engineer")
- Logo for documents: the navy/teal "MW" line logo on the pads (`logo fainal/YOUR GATEWAY T.svg`), not the glossy 3D `LOGO.png`
- Colours on the pads: navy header/table, teal accents
- Technology partners line: Siemens · Mitsubishi Electric · Delta · Omron
- **Missing (owner):** BIN, TIN, trade licence no., full address, bank name / account / branch / routing, bKash/Nagad merchant no.

## Existing reference formats on the pads (website numbers stay as built, `MW-` series)
- Quotation ref: `MW-<client>-Q-<ddmmyy>`; invoice: `MW-<client>-INV-<ddmmyy>`, with "Against Quotation No. …"

## Terms on the owner's current quotation pad (draft T&C for website quotes)
1. Offer validity: 7 working days from submission — **website default is 15 days (ADR-0011 §15); owner to pick one**
2. VAT & tax: excluded on the pad — the website shows VAT-inclusive prices (ADR-0008); PDF will say "VAT included as shown"
3. Payment: 70 % advance, 30 % on completion / as agreed
4. Delivery / lead time: 7 working days after the advance is received
5. Warranty: product 365 days, software 1825 days
6. Food & accommodation (site work): customer's scope

## Invoice pad terms
- Payment due within 7 days of the invoice; mention the invoice no. on every payment; late payment charge as mutually agreed.

## Owner decisions, 2026-11-20
- Quote validity: **15 days** on the website (ADR-0011 §15 stays); the PDF prints the real valid-until date.
- VAT wording "Prices include VAT as shown": approved.
- The pad's "70 % advance, 30 % on completion / as agreed" line stays on the PDF **and staff must be able to change the default terms** → new unit "quote terms settings" (console page + stored defaults; per-quote "Terms for this quotation" already exists).
- Hosting: owner asked for the cheapest option that can grow → recommended a VPS (8 GB RAM, Singapore region) running the H3a Docker compose; resize later. Owner to buy.
