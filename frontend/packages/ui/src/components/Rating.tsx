'use client';

import React from 'react';
import { Star, StarHalf } from 'lucide-react';
import { cn } from '../utils';

export interface RatingProps extends React.HTMLAttributes<HTMLDivElement> {
  value: number; // 0 to 5
  count?: number;
  size?: 'sm' | 'md' | 'lg';
  showCount?: boolean;
  interactive?: boolean;
  onRatingChange?: (rating: number) => void;
}

const starSizes = {
  sm: 'h-3.5 w-3.5',
  md: 'h-4 w-4',
  lg: 'h-5 w-5',
};

const textSizes = {
  sm: 'text-xs',
  md: 'text-sm',
  lg: 'text-base font-medium',
};

export const Rating: React.FC<RatingProps> = ({
  value,
  count,
  size = 'md',
  showCount = true,
  interactive = false,
  onRatingChange,
  className,
  ...props
}) => {
  const [hoverValue, setHoverValue] = React.useState<number | null>(null);
  const displayValue = hoverValue !== null ? hoverValue : Math.min(Math.max(value, 0), 5);

  const fullStars = Math.floor(displayValue);
  const hasHalfStar = displayValue - fullStars >= 0.25 && displayValue - fullStars < 0.75;
  const emptyStars = 5 - fullStars - (hasHalfStar ? 1 : 0);

  const starClass = starSizes[size];

  return (
    <div
      className={cn('inline-flex items-center gap-1.5 select-none', className)}
      role={interactive ? 'radiogroup' : 'img'}
      aria-label={`Rated ${value.toFixed(1)} out of 5 stars${count ? ` across ${count.toLocaleString()} ratings` : ''}`}
      {...props}
    >
      <div className="flex items-center text-amber-400">
        {[...Array(fullStars)].map((_, i) => (
          <button
            key={`full-${i}`}
            type="button"
            disabled={!interactive}
            onClick={() => interactive && onRatingChange?.(i + 1)}
            onMouseEnter={() => interactive && setHoverValue(i + 1)}
            onMouseLeave={() => interactive && setHoverValue(null)}
            className={cn('p-0 border-0 bg-transparent', interactive && 'cursor-pointer hover:scale-110 transition-transform')}
            aria-label={`${i + 1} Star`}
          >
            <Star className={cn(starClass, 'fill-amber-400 stroke-amber-400')} />
          </button>
        ))}

        {hasHalfStar && (
          <button
            type="button"
            disabled={!interactive}
            onClick={() => interactive && onRatingChange?.(fullStars + 0.5)}
            className={cn('p-0 border-0 bg-transparent relative', interactive && 'cursor-pointer')}
            aria-label={`${fullStars + 0.5} Stars`}
          >
            <StarHalf className={cn(starClass, 'fill-amber-400 stroke-amber-400')} />
          </button>
        )}

        {[...Array(Math.max(0, emptyStars))].map((_, i) => {
          const starIndex = fullStars + (hasHalfStar ? 1 : 0) + i + 1;
          return (
            <button
              key={`empty-${i}`}
              type="button"
              disabled={!interactive}
              onClick={() => interactive && onRatingChange?.(starIndex)}
              onMouseEnter={() => interactive && setHoverValue(starIndex)}
              onMouseLeave={() => interactive && setHoverValue(null)}
              className={cn('p-0 border-0 bg-transparent text-text-subtle/40', interactive && 'cursor-pointer hover:scale-110 transition-transform')}
              aria-label={`${starIndex} Star`}
            >
              <Star className={cn(starClass, 'stroke-current')} />
            </button>
          );
        })}
      </div>

      {showCount && (
        <span className={cn('text-text-muted font-sans', textSizes[size])}>
          <strong className="text-text font-semibold">{value.toFixed(1)}</strong>
          {count !== undefined && (
            <span className="text-text-subtle"> ({count > 999 ? `${(count / 1000).toFixed(1)}k` : count})</span>
          )}
        </span>
      )}
    </div>
  );
};
