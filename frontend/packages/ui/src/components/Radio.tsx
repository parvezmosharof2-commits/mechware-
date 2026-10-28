'use client';

import React from 'react';
import * as RadioGroupPrimitive from '@radix-ui/react-radio-group';
import { Circle } from 'lucide-react';
import { cn } from '../utils';

export const RadioGroup = React.forwardRef<
  React.ElementRef<typeof RadioGroupPrimitive.Root>,
  React.ComponentPropsWithoutRef<typeof RadioGroupPrimitive.Root>
>(({ className, ...props }, ref) => {
  return (
    <RadioGroupPrimitive.Root
      className={cn('grid gap-2.5 font-sans', className)}
      {...props}
      ref={ref}
    />
  );
});
RadioGroup.displayName = RadioGroupPrimitive.Root.displayName;

export interface RadioItemProps
  extends React.ComponentPropsWithoutRef<typeof RadioGroupPrimitive.Item> {
  label?: React.ReactNode;
  description?: React.ReactNode;
}

export const Radio = React.forwardRef<
  React.ElementRef<typeof RadioGroupPrimitive.Item>,
  RadioItemProps
>(({ className, label, description, id, ...props }, ref) => {
  const generatedId = React.useId();
  const radioId = id || generatedId;

  return (
    <div className="flex items-start gap-2.5">
      <RadioGroupPrimitive.Item
        ref={ref}
        id={radioId}
        className={cn(
          'aspect-square h-4 w-4 rounded-full border border-surface-border-strong bg-bg-muted text-accent outline-none select-none transition-all duration-fast mt-0.5',
          'focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-2 focus-visible:ring-offset-bg',
          'disabled:cursor-not-allowed disabled:opacity-50',
          'data-[state=checked]:border-accent data-[state=checked]:bg-bg-muted',
          className
        )}
        {...props}
      >
        <RadioGroupPrimitive.Indicator className="flex items-center justify-center">
          <Circle className="h-2.5 w-2.5 fill-accent text-accent" />
        </RadioGroupPrimitive.Indicator>
      </RadioGroupPrimitive.Item>

      {(label || description) && (
        <div className="flex flex-col text-sm leading-tight select-none">
          {label && (
            <label
              htmlFor={radioId}
              className="font-medium text-text cursor-pointer hover:text-accent transition-colors"
            >
              {label}
            </label>
          )}
          {description && (
            <span className="text-xs text-text-muted mt-0.5">{description}</span>
          )}
        </div>
      )}
    </div>
  );
});
Radio.displayName = RadioGroupPrimitive.Item.displayName;
