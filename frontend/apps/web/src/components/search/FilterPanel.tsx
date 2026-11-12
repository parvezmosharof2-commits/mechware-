'use client';

import * as React from 'react';
import { Button, Checkbox, Input } from '@mechware/ui';
import { useTranslations } from 'next-intl';
import type { SearchParams } from '@mechware/api-client';
import { minorToTaka, takaToMinor, toggleBrand, withFilter } from '../../lib/search-ui';

export interface FilterPanelProps {
  idPrefix: string;
  params: SearchParams;
  brands: { slug: string; label: string; count: number }[];
  categories: { value: string; label: string; count: number }[];
  onChange: (next: SearchParams) => void;
}

export function FilterPanel({ idPrefix, params, brands, categories, onChange }: FilterPanelProps) {
  const t = useTranslations('Search');
  const [min, setMin] = React.useState(minorToTaka(params.minPriceMinor));
  const [max, setMax] = React.useState(minorToTaka(params.maxPriceMinor));

  // The URL is the source of truth: when it changes (chip removed, back button), the inputs follow.
  React.useEffect(() => setMin(minorToTaka(params.minPriceMinor)), [params.minPriceMinor]);
  React.useEffect(() => setMax(minorToTaka(params.maxPriceMinor)), [params.maxPriceMinor]);

  const minMinor = takaToMinor(min);
  const maxMinor = takaToMinor(max);
  const invalid = (min !== '' && minMinor === undefined) || (max !== '' && maxMinor === undefined);
  const inverted = minMinor !== undefined && maxMinor !== undefined && minMinor > maxMinor;

  const applyPrice = (e: React.FormEvent) => {
    e.preventDefault();
    if (invalid || inverted) return;
    onChange(withFilter(params, { minPriceMinor: minMinor, maxPriceMinor: maxMinor }));
  };

  const legend = 'text-sm font-semibold text-text mb-2';
  return (
    <div className="space-y-6">
      <fieldset>
        <legend className={legend}>{t('availability')}</legend>
        <Checkbox
          id={`${idPrefix}-instock`}
          label={t('inStockOnly')}
          checked={Boolean(params.inStock)}
          onCheckedChange={(v) => onChange(withFilter(params, { inStock: v === true ? true : undefined }))}
        />
      </fieldset>

      {brands.length > 0 && (
        <fieldset>
          <legend className={legend}>{t('brand')}</legend>
          <div className="space-y-2 max-h-64 overflow-y-auto pr-1">
            {brands.map((b) => (
              <Checkbox
                key={b.slug}
                id={`${idPrefix}-brand-${b.slug}`}
                label={`${b.label} (${b.count})`}
                checked={params.brand?.includes(b.slug) ?? false}
                onCheckedChange={() => onChange(toggleBrand(params, b.slug))}
              />
            ))}
          </div>
        </fieldset>
      )}

      {categories.length > 0 && (
        <fieldset>
          <legend className={legend}>{t('category')}</legend>
          <div className="space-y-2 max-h-64 overflow-y-auto pr-1">
            {categories.map((c) => (
              <Checkbox
                key={c.value}
                id={`${idPrefix}-cat-${c.value}`}
                label={`${c.label} (${c.count})`}
                checked={params.category === c.value}
                onCheckedChange={(v) => onChange(withFilter(params, { category: v === true ? c.value : undefined }))}
              />
            ))}
          </div>
        </fieldset>
      )}

      <form onSubmit={applyPrice}>
        <fieldset>
          <legend className={legend}>{t('priceRange')}</legend>
          <div className="flex items-start gap-2">
            <Input
              id={`${idPrefix}-min`}
              label={t('minPrice')}
              inputMode="decimal"
              prefix="৳"
              value={min}
              onChange={(e) => setMin(e.target.value)}
            />
            <Input
              id={`${idPrefix}-max`}
              label={t('maxPrice')}
              inputMode="decimal"
              prefix="৳"
              value={max}
              onChange={(e) => setMax(e.target.value)}
            />
          </div>
          {(invalid || inverted) && (
            <p role="alert" className="mt-2 text-xs text-fault font-medium">
              {invalid ? t('priceInvalid') : t('priceInverted')}
            </p>
          )}
          <Button type="submit" variant="secondary" size="sm" className="mt-3" disabled={invalid || inverted}>
            {t('applyPrice')}
          </Button>
        </fieldset>
      </form>
    </div>
  );
}
