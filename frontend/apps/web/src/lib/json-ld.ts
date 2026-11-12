import type { ProductDetail } from '@mechware/api-client';

/**
 * Serialises structured data for a `<script type="application/ld+json">` body. `<`, `>`, `&` and the JS line separators
 * are written as unicode escapes, so text from a product (e.g. `</script>` in a name) can never close the tag early.
 */
export function jsonLdString(data: unknown): string {
  return JSON.stringify(data)
    .replace(/</g, '\\u003c')
    .replace(/>/g, '\\u003e')
    .replace(/&/g, '\\u0026')
    .replace(new RegExp(String.fromCharCode(0x2028), 'g'), '\\u2028')
    .replace(new RegExp(String.fromCharCode(0x2029), 'g'), '\\u2029');
}

export function truncateText(text: string, max: number): string {
  const clean = text.replace(/\s+/g, ' ').trim();
  if (clean.length <= max) return clean;
  return `${clean.slice(0, max - 1).trimEnd()}…`;
}

/** Poisha to a taka decimal string: 123450 -> "1234.50". */
export function minorToDecimal(minor: number): string {
  return (Math.trunc(minor) / 100).toFixed(2);
}

export type ProductJsonLdInput = {
  product: ProductDetail;
  title: string;
  /** Plain text (already stripped of markup); cut to 500 characters here. */
  description: string;
  /** Absolute canonical URL of this product page. */
  url: string;
  /** Origin that API-relative media URLs are resolved against. */
  mediaBase: string;
};

function availability(product: ProductDetail, status: string): string {
  if (product.supplyStatus === 'obsolete') return 'https://schema.org/Discontinued';
  return status === 'out_of_stock' ? 'https://schema.org/OutOfStock' : 'https://schema.org/InStock';
}

function absoluteMedia(url: string, base: string): string {
  return /^https?:\/\//i.test(url) ? url : `${base.replace(/\/+$/, '')}${url.startsWith('/') ? url : `/${url}`}`;
}

/** schema.org Product with offers, built only from fields the storefront DTO has. Variants priced 0 ("on request") get no offer. */
export function buildProductJsonLd({ product, title, description, url, mediaBase }: ProductJsonLdInput): Record<string, unknown> {
  const variants = product.variants;
  const priced = variants.filter((v) => v.pricing.price.amountMinor > 0);
  const currency = priced[0]?.pricing.price.currency ?? 'BDT';
  const offerFor = (v: (typeof variants)[number]) => ({
    '@type': 'Offer',
    sku: v.sku,
    priceCurrency: v.pricing.price.currency,
    price: minorToDecimal(v.pricing.price.amountMinor),
    availability: availability(product, v.stock.status),
    url: variants.length > 1 ? `${url}?v=${encodeURIComponent(v.sku)}` : url,
    itemCondition: product.condition.toLowerCase() === 'new' ? 'https://schema.org/NewCondition' : undefined
  });

  const prices = priced.map((v) => v.pricing.price.amountMinor);
  const anyAvailable = priced.some((v) => availability(product, v.stock.status) === 'https://schema.org/InStock');
  let offers: Record<string, unknown> | undefined;
  if (priced.length === 1) offers = offerFor(priced[0]!);
  else if (priced.length > 1) {
    offers = {
      '@type': 'AggregateOffer',
      priceCurrency: currency,
      lowPrice: minorToDecimal(Math.min(...prices)),
      highPrice: minorToDecimal(Math.max(...prices)),
      offerCount: priced.length,
      availability: anyAvailable ? 'https://schema.org/InStock' : availability(product, 'out_of_stock'),
      offers: priced.map(offerFor)
    };
  }

  const defaultVariant = variants.find((v) => v.isDefault) ?? variants[0];
  const mpn = defaultVariant?.manufacturerPartNumber?.trim();
  const images = product.media.filter((m) => m.type === 'image' || !m.type).map((m) => absoluteMedia(m.url, mediaBase));
  return {
    '@context': 'https://schema.org',
    '@type': 'Product',
    name: title,
    sku: defaultVariant?.sku,
    mpn: mpn || undefined,
    brand: product.brand ? { '@type': 'Brand', name: product.brand.name } : undefined,
    image: images.length ? images : undefined,
    description: description ? truncateText(description, 500) : undefined,
    url,
    offers,
    aggregateRating:
      product.ratingCount > 0
        ? { '@type': 'AggregateRating', ratingValue: product.ratingAvg, reviewCount: product.ratingCount }
        : undefined
  };
}

export function buildBreadcrumbJsonLd(items: { name: string; url: string }[]): Record<string, unknown> {
  return {
    '@context': 'https://schema.org',
    '@type': 'BreadcrumbList',
    itemListElement: items.map((item, i) => ({ '@type': 'ListItem', position: i + 1, name: item.name, item: item.url }))
  };
}
