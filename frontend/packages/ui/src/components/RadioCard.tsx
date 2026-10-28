'use client';

import React from 'react';
import * as RadioGroupPrimitive from '@radix-ui/react-radio-group';
import { Circle } from 'lucide-react';
import { cn } from '../utils';

export interface RadioCardProps
  extends Omit<React.ComponentPropsWithoutRef<typeof RadioGroupPrimitive.Item>, 'title'> {
  title: React.ReactNode;
  description?: React.ReactNode;
  badge?: React.ReactNode;
  price?: React.ReactNode;
  icon?: React.ReactNode;
}

export const RadioCard = React.forwardRef<
  React.ElementRef<typeof RadioGroupPrimitive.Item>,
  RadioCardProps
>(({ className, title, description, badge, price, icon, id, ...props }, ref) => {
  const generatedId = React.useId();
  const radioId = id || generatedId;

  return (
    <RadioGroupPrimitive.Item
      ref={ref}
      id={radioId}
      className={cn(
        'relative flex items-center justify-between w-full rounded-lg border border-surface-border bg-surface p-4 text-left outline-none transition-all duration-fast select-none cursor-pointer',
        'hover:border-surface-border-strong hover:bg-bg-subtle',
        'focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-2 focus-visible:ring-offset-bg',
        'data-[state=checked]:border-accent data-[state=checked]:bg-accent/5 data-[state=checked]:shadow-glow-cyan-sm',
        'disabled:cursor-not-allowed disabled:opacity-50',
        className
      )}
      {...props}
    >
      <div className="flex items-center gap-3.5">
        <div className="flex h-5 w-5 shrink-0 items-center justify-center rounded-full border border-surface-border-strong bg-bg-muted text-accent data-[state=checked]:border-accent">
          <RadioGroupPrimitive.Indicator className="flex items-center justify-center">
            <Circle className="h-3 w-3 fill-accent text-accent" />
          </RadioGroupPrimitive.Indicator>
        </div>

        {icon && <div className="shrink-0 text-text">{icon}</div>}

        <div className="flex flex-col">
          <div className="flex items-center gap-2">
            <span className="font-semibold text-text text-sm">{title}</span>
            {badge && <span>{badge}</span>}
          </div>
          {description && (
            <span className="text-xs text-text-muted mt-0.5">{description}</span>
          )}
        </div>
      </div>

      {price && <div className="text-sm font-semibold text-text tabular-nums">{price}</div>}
    </RadioGroupPrimitive.Item>
  );
});
RadioCard.displayName = 'RadioCard';
