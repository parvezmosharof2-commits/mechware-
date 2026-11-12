'use client';

import * as React from 'react';
import Image from 'next/image';
import { useLocale, useTranslations } from 'next-intl';
import { Badge } from '@mechware/ui/src/components/Badge';
import { Button } from '@mechware/ui/src/components/Button';
import { formatPriceMinor, type MediaDto, type ProductDetail, type VariantDto } from '@mechware/api-client';
import { Availability } from '../search/hit-parts';
import { CopyPartNumber } from '../search/CopyPartNumber';
import {
  absoluteMediaUrl,
  clampQuantity,
  galleryFor,
  minQuantity,
  pickLocalized,
  pickVariant,
  productTitle,
  variantLabel
} from '../../lib/product-ui';

export interface ProductViewProps {
  product: ProductDetail;
  /** Selected variant SKU from `?v=`; the server passes it so the first paint already shows that variant. */
  initialSku?: string;
  /** Public base URL for relative media paths. */
  mediaBase: string;
  /** Server-rendered lifecycle banner and static sections (description, specs, documents); they are never hydrated. */
  banner?: React.ReactNode;
  sections?: React.ReactNode;
}

type Translate = ReturnType<typeof useTranslations>;

function Gallery({ images, title, mediaBase, t }: { images: MediaDto[]; title: string; mediaBase: string; t: Translate }) {
  const [index, setIndex] = React.useState(0);
  React.useEffect(() => setIndex(0), [images]);
  if (images.length === 0) {
    return (
      <div className="flex aspect-square items-center justify-center rounded-lg border border-surface-border bg-bg-muted text-sm text-text-muted">
        {t('noImage')}
      </div>
    );
  }
  const current = images[Math.min(index, images.length - 1)]!;
  return (
    <div>
      <div className="overflow-hidden rounded-lg border border-surface-border bg-bg-muted">
        {/* unoptimized: product media is served by the API/CDN as-is (often SVG or already sized), so no optimizer hop. */}
        <Image
          src={absoluteMediaUrl(current.url, mediaBase)}
          alt={current.alt || title}
          width={current.width || 1200}
          height={current.height || 1200}
          sizes="(min-width: 1024px) 560px, 100vw"
          className="aspect-square w-full object-contain"
          priority
          unoptimized
        />
      </div>
      {images.length > 1 && (
        <ul className="mt-3 flex gap-2 overflow-x-auto" aria-label={t('thumbnails')}>
          {images.map((m, i) => (
            <li key={m.url}>
              <button
                type="button"
                onClick={() => setIndex(i)}
                aria-label={t('showImage', { n: i + 1, total: images.length })}
                aria-current={i === index}
                className={`block h-16 w-16 overflow-hidden rounded-md border bg-bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent ${
                  i === index ? 'border-accent' : 'border-surface-border'
                }`}
              >
                <Image
                  src={absoluteMediaUrl(m.url, mediaBase)}
                  alt=""
                  width={64}
                  height={64}
                  loading="lazy"
                  className="h-full w-full object-contain"
                  unoptimized
                />
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

export function ProductView({ product, initialSku, mediaBase, banner, sections }: ProductViewProps) {
  const t = useTranslations('Product');
  const locale = useLocale();
  const [sku, setSku] = React.useState<string | undefined>(initialSku);
  const variant: VariantDto | undefined = pickVariant(product.variants, sku);
  const title = productTitle(product, locale);
  const [qtyText, setQtyText] = React.useState(() => String(variant ? minQuantity(variant) : 1));

  const selectVariant = (next: VariantDto) => {
    setSku(next.sku);
    setQtyText(String(minQuantity(next)));
    // The URL is the shareable state; replaceState avoids a server round-trip for data already on the page.
    try {
      const url = new URL(window.location.href);
      url.searchParams.set('v', next.sku);
      window.history.replaceState(window.history.state, '', url);
    } catch {
      /* not fatal: the selection still works without the URL */
    }
  };

  const images = galleryFor(product.media, variant);
  const pricing = variant?.pricing;
  const onRequest = !pricing || pricing.price.amountMinor === 0;
  const currency = pricing?.price.currency ?? 'BDT';
  const showCompare = Boolean(pricing?.compareAt && pricing.compareAt.amountMinor > pricing.price.amountMinor);
  const stock = variant?.stock;
  const stockStatus = stock?.status ?? 'out_of_stock';
  const pack = Math.max(1, variant?.packSize ?? 1);
  const min = variant ? minQuantity(variant) : 1;
  const partNumber = variant?.manufacturerPartNumber?.trim() || variant?.sku || '';
  const short = pickLocalized(locale, product.shortDescriptionEn, product.shortDescriptionBn);

  return (
    <>
      {banner}
      <div className="grid gap-8 lg:grid-cols-2">
      <Gallery images={images} title={title} mediaBase={mediaBase} t={t} />

      <div>
        <h1 className="font-display text-2xl font-bold text-text md:text-3xl">{title}</h1>

        <dl className="mt-3 space-y-1 text-sm">
          {product.brand && (
            <div className="flex gap-2">
              <dt className="text-text-muted">{t('brand')}</dt>
              <dd className="font-medium text-text">{product.brand.name}</dd>
            </div>
          )}
          {variant && (
            <div className="flex items-center gap-1">
              <dt className="text-text-muted">{t('partNumber')}</dt>
              <dd className="flex items-center gap-1 font-mono text-text">
                {partNumber}
                <CopyPartNumber value={partNumber} label={t('copyPartNumber', { part: partNumber })} copiedLabel={t('copied')} />
              </dd>
            </div>
          )}
          {variant && partNumber !== variant.sku && (
            <div className="flex gap-2 text-xs">
              <dt className="text-text-muted">{t('sku')}</dt>
              <dd className="font-mono text-text-muted">{variant.sku}</dd>
            </div>
          )}
        </dl>

        <div className="mt-3 flex flex-wrap gap-1" aria-live="polite">
          <Availability hit={{ inStock: stockStatus !== 'out_of_stock', supplyStatus: product.supplyStatus }} t={t as never} />
          {stockStatus === 'low_stock' && (
            <Badge variant="low-stock" size="sm">
              {t('lowStock')}
            </Badge>
          )}
        </div>

        {short && <p className="mt-4 text-text-muted">{short}</p>}

        {product.variants.length > 1 && (
          <fieldset className="mt-5">
            <legend className="mb-2 text-sm font-medium text-text">{t('variant')}</legend>
            <div className="flex flex-wrap gap-2">
              {product.variants.map((v) => {
                const checked = v.sku === variant?.sku;
                return (
                  <label
                    key={v.id}
                    className={`cursor-pointer rounded-md border px-3 py-2 text-sm has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-accent ${
                      checked ? 'border-accent bg-accent/10 text-text' : 'border-surface-border-strong text-text-muted hover:border-accent'
                    }`}
                  >
                    <input type="radio" name="variant" value={v.sku} checked={checked} onChange={() => selectVariant(v)} className="sr-only" />
                    {variantLabel(v)}
                  </label>
                );
              })}
            </div>
          </fieldset>
        )}

        <div className="mt-6 rounded-lg border border-surface-border bg-bg-muted p-4">
          {onRequest || !pricing ? (
            <p className="text-2xl font-bold text-text">{t('priceOnRequest')}</p>
          ) : (
            <>
              <p className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
                <span className="text-3xl font-bold text-text">{formatPriceMinor(pricing.price.amountMinor, currency, locale)}</span>
                {showCompare && pricing.compareAt && (
                  <>
                    <s className="text-text-muted">
                      <span className="sr-only">{t('listPrice')} </span>
                      {formatPriceMinor(pricing.compareAt.amountMinor, currency, locale)}
                    </s>
                    {pricing.discountPercent > 0 && (
                      <Badge variant="deal" size="sm">
                        {t('discount', { percent: pricing.discountPercent })}
                      </Badge>
                    )}
                  </>
                )}
              </p>
              <p className="mt-1 text-xs text-text-muted">{pricing.taxIncluded ? t('vatIncluded') : t('vatExcluded')}</p>
            </>
          )}
        </div>

        <div className="mt-5">
          <label htmlFor="pdp-qty" className="mb-1 block text-sm font-medium text-text">
            {t('quantity')}
          </label>
          <input
            id="pdp-qty"
            type="number"
            inputMode="numeric"
            min={min}
            step={pack}
            value={qtyText}
            onChange={(e) => setQtyText(e.target.value)}
            onBlur={() => variant && setQtyText(String(clampQuantity(Number(qtyText), variant)))}
            aria-describedby="pdp-qty-hint"
            className="h-11 w-28 rounded-md border border-surface-border-strong bg-bg-muted px-3 text-sm text-text outline-none focus-visible:ring-2 focus-visible:ring-accent"
          />
          <p id="pdp-qty-hint" className="mt-1 text-xs text-text-muted">
            {pack > 1 ? t('qtyHintPack', { min, pack }) : min > 1 ? t('qtyHintMin', { min }) : t('qtyHintNone')}
          </p>
        </div>

        <div className="mt-4">
          {/* A disabled button gets no tooltip or focus, so the reason is also visible text, tied on by aria-describedby. */}
          <span title={t('comingSoon')} className="inline-block">
            <Button type="button" variant="accent" size="lg" disabled aria-describedby="pdp-quote-note">
              {t('addToQuote')}
            </Button>
          </span>
          <p id="pdp-quote-note" className="mt-2 text-xs text-text-muted">
            {t('comingSoon')}
          </p>
        </div>

        <dl className="mt-6 grid grid-cols-2 gap-3 text-sm">
          <div>
            <dt className="text-text-muted">{t('warranty')}</dt>
            <dd className="text-text">{t('warrantyMonths', { count: product.warrantyMonths })}</dd>
          </div>
          <div>
            <dt className="text-text-muted">{t('seller')}</dt>
            <dd className="text-text">{product.seller.storeName}</dd>
          </div>
        </dl>
      </div>
      </div>
      <div className="mt-10">
        {sections}
        <section id="pdp-availability" aria-labelledby="pdp-availability-h" className="mt-8 scroll-mt-14">
          <h2 id="pdp-availability-h" className="font-display text-xl font-bold text-text">
            {t('availability')}
          </h2>
          {stock ? (
            <div className="mt-3 text-sm" aria-live="polite">
              <p className="font-medium text-text">{t('availableUnits', { count: Math.max(0, stock.available) })}</p>
              {stock.status === 'out_of_stock' && <p className="mt-1 text-text-muted">{t('outOfStockNotice')}</p>}
              {stock.status === 'low_stock' && <p className="mt-1 text-text-muted">{t('lowStockNotice')}</p>}
            </div>
          ) : (
            <p className="mt-3 text-sm text-text-muted">{t('outOfStockNotice')}</p>
          )}
        </section>
      </div>
    </>
  );
}
