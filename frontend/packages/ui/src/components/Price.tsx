'use client';

import React from 'react';
import { cn } from '../utils';
import { formatCurrency } from '../formatters';

export { formatCurrency };

export interface PriceProps extends React.HTMLAttributes<HTMLDivElement> {
  amount: number | string;
  compareAt?: number | string;
  currency?: 'BDT' | 'USD';
  locale?: string;
  size?: 'sm' | 'md' | 'lg' | 'xl';
  showDiscountBadge?: boolean;
}

const sizeStyles = {
  sm: {
    amount: 'text-sm font-semibold',
    compare: 'text-xs',
    badge: 'text-[10px] px-1 py-0.5',
  },
  md: {
    amount: 'text-lg font-bold',
    compare: 'text-sm',
    badge: 'text-xs px-1.5 py-0.5',
  },
  lg: {
    amount: 'text-2xl font-bold',
    compare: 'text-base',
    badge: 'text-xs px-2 py-0.5',
  },
  xl: {
    amount: 'text-3xl lg:text-4xl font-extrabold',
    compare: 'text-lg',
    badge: 'text-sm px-2.5 py-1',
  },
};

export const Price: React.FC<PriceProps> = ({
  amount,
  compareAt,
  currency = 'BDT',
  locale = 'en',
  size = 'md',
  showDiscountBadge = true,
  className,
  ...props
}) => {
  const numericAmount = typeof amount === 'string' ? parseFloat(amount) : amount;
  const numericCompareAt =
    compareAt !== undefined
      ? typeof compareAt === 'string'
        ? parseFloat(compareAt)
        : compareAt
      : undefined;

  const hasDiscount = numericCompareAt !== undefined && numericCompareAt > numericAmount;
  const discountPercent = hasDiscount
    ? Math.floor(((numericCompareAt - numericAmount) / numericCompareAt) * 100)
    : 0;

  const currentStyles = sizeStyles[size];

  return (
    <div
      className={cn('inline-flex items-baseline gap-2 flex-wrap font-sans', className)}
      {...props}
    >
      <span className={cn('tabular-nums text-text tracking-tight', currentStyles.amount)}>
        {formatCurrency(numericAmount, currency, locale)}
      </span>

      {hasDiscount && (
        <>
          <span
            className={cn(
              'tabular-nums text-text-subtle line-through decoration-text-subtle/60',
              currentStyles.compare
            )}
            aria-label={`Original price: ${formatCurrency(numericCompareAt, currency, locale)}`}
          >
            {formatCurrency(numericCompareAt, currency, locale)}
          </span>

          {showDiscountBadge && discountPercent > 0 && (
            <span
              className={cn(
                'inline-flex items-center font-bold rounded-sm bg-fault/15 text-fault border border-fault/30',
                currentStyles.badge
              )}
            >
              -{discountPercent}%
            </span>
          )}
        </>
      )}
    </div>
  );
};
