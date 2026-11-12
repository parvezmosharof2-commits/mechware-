'use client';

import { Button, Sheet, SheetContent, SheetDescription, SheetTitle } from '@mechware/ui';
import { useTranslations } from 'next-intl';

export interface FilterDrawerProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  total: number;
  children: React.ReactNode;
}

/** Radix Dialog: traps focus, closes on Escape, returns focus to the Filters button. Loaded lazily (mobile only). */
export default function FilterDrawer({ open, onOpenChange, total, children }: FilterDrawerProps) {
  const t = useTranslations('Search');
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent side="left" className="overflow-y-auto">
        <SheetTitle>{t('filters')}</SheetTitle>
        <SheetDescription className="sr-only">{t('filtersDescription')}</SheetDescription>
        <div className="mt-4">{children}</div>
        <Button type="button" variant="accent" className="mt-6" onClick={() => onOpenChange(false)}>
          {t('showResults', { count: total })}
        </Button>
      </SheetContent>
    </Sheet>
  );
}
