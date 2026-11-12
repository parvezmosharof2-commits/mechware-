import type { BreadcrumbDto, MediaDto, ProductDetail, VariantDto } from '@mechware/api-client';

/** Path with the locale prefix the router uses (`localePrefix: as-needed`: English has none). */
export function localePath(locale: string, path: string): string {
  return locale === 'bn' ? `/bn${path}` : path;
}

export function productPath(locale: string, slug: string): string {
  return localePath(locale, `/products/${encodeURIComponent(slug)}`);
}

/** Bangla text when present and the locale is bn, otherwise the English text. */
export function pickLocalized(locale: string, en: string, bn: string): string {
  return locale === 'bn' && bn.trim() ? bn : en;
}

export function productTitle(p: Pick<ProductDetail, 'titleEn' | 'titleBn'>, locale: string): string {
  return pickLocalized(locale, p.titleEn, p.titleBn);
}

export function breadcrumbLabel(b: BreadcrumbDto, locale: string): string {
  return pickLocalized(locale, b.nameEn, b.nameBn);
}

/** The variant named by `?v=<sku>` (case-insensitive), else the default one, else the first. Undefined for no variants. */
export function pickVariant(variants: VariantDto[], sku: string | null | undefined): VariantDto | undefined {
  const wanted = sku?.trim().toLowerCase();
  return (
    (wanted ? variants.find((v) => v.sku.toLowerCase() === wanted) : undefined) ??
    variants.find((v) => v.isDefault) ??
    variants[0]
  );
}

/** Images to show for a variant: its own when it has any, otherwise the shared ones; all images as a last resort. */
export function galleryFor(media: MediaDto[], variant: VariantDto | undefined): MediaDto[] {
  const images = media.filter((m) => m.type === 'image');
  const own = variant ? images.filter((m) => m.variantId === variant.id) : [];
  if (own.length > 0) return own;
  const shared = images.filter((m) => !m.variantId);
  return shared.length > 0 ? shared : images;
}

/** Smallest valid quantity: the MOQ rounded up to a whole number of packs. */
export function minQuantity(v: Pick<VariantDto, 'minOrderQty' | 'packSize'>): number {
  const pack = Math.max(1, v.packSize);
  return Math.max(pack, Math.ceil(Math.max(1, v.minOrderQty) / pack) * pack);
}

/** Snaps typed input to the nearest valid quantity at or above the minimum (a multiple of the pack size). */
export function clampQuantity(raw: number, v: Pick<VariantDto, 'minOrderQty' | 'packSize'>): number {
  const pack = Math.max(1, v.packSize);
  const min = minQuantity(v);
  if (!Number.isFinite(raw)) return min;
  return Math.max(min, Math.ceil(Math.trunc(raw) / pack) * pack);
}

/** "key: value" option text for a variant, e.g. "Voltage: 24V, Size: M"; the SKU when it has no options. */
export function variantLabel(v: VariantDto): string {
  const parts = Object.entries(v.options).map(([k, val]) => `${k}: ${val}`);
  return parts.length ? parts.join(', ') : v.sku;
}

/** Media URLs from the API may be relative to it; make them absolute against the public API base. */
export function absoluteMediaUrl(url: string, apiBase: string): string {
  if (/^(https?:)?\/\//i.test(url) || url.startsWith('data:')) return url;
  return `${apiBase.replace(/\/$/, '')}${url.startsWith('/') ? '' : '/'}${url}`;
}

/** "true"/"false"/"yes"/"no" spec values as booleans; undefined for anything else (ranges and units arrive pre-formatted). */
export function specBoolean(value: string): boolean | undefined {
  const v = value.trim().toLowerCase();
  if (v === 'true' || v === 'yes') return true;
  if (v === 'false' || v === 'no') return false;
  return undefined;
}

/** "1.2 MB" / "340 KB" / "512 B"; empty for a missing or zero size. */
export function formatFileSize(bytes: number | null | undefined): string {
  if (!bytes || bytes <= 0 || !Number.isFinite(bytes)) return '';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/** Spec groups with at least one non-empty row, in API order. */
export function specGroups(specs: Record<string, Record<string, string>> | null | undefined): [string, [string, string][]][] {
  return Object.entries(specs ?? {})
    .map(([group, rows]) => [group, Object.entries(rows ?? {}).filter(([, v]) => v !== null && String(v).trim() !== "")] as [string, [string, string][]])
    .filter(([, rows]) => rows.length > 0);
}
