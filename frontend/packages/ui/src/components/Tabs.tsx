'use client';

import React from 'react';
import * as TabsPrimitive from '@radix-ui/react-tabs';
import { cn } from '../utils';

export const Tabs = TabsPrimitive.Root;

export const TabsList = React.forwardRef<
  React.ElementRef<typeof TabsPrimitive.List>,
  React.ComponentPropsWithoutRef<typeof TabsPrimitive.List> & {
    variant?: 'pill' | 'line';
  }
>(({ className, variant = 'pill', ...props }, ref) => (
  <TabsPrimitive.List
    ref={ref}
    className={cn(
      'inline-flex max-w-full items-center overflow-x-auto text-text-muted select-none font-sans',
      variant === 'pill'
        ? 'h-10 rounded-md bg-bg-muted p-1 border border-surface-border'
        : 'h-11 border-b border-surface-border gap-6 w-full justify-start rounded-none bg-transparent p-0',
      className
    )}
    {...props}
  />
));
TabsList.displayName = TabsPrimitive.List.displayName;

export const TabsTrigger = React.forwardRef<
  React.ElementRef<typeof TabsPrimitive.Trigger>,
  React.ComponentPropsWithoutRef<typeof TabsPrimitive.Trigger> & {
    variant?: 'pill' | 'line';
  }
>(({ className, variant = 'pill', ...props }, ref) => (
  <TabsPrimitive.Trigger
    ref={ref}
    className={cn(
      'inline-flex items-center justify-center whitespace-nowrap text-sm font-medium transition-all duration-fast outline-none disabled:pointer-events-none disabled:opacity-50 cursor-pointer',
      variant === 'pill'
        ? 'rounded-sm px-3.5 py-1.5 hover:text-text focus-visible:ring-2 focus-visible:ring-accent data-[state=active]:bg-surface data-[state=active]:text-text data-[state=active]:shadow-sm data-[state=active]:border data-[state=active]:border-surface-border'
        : 'h-full border-b-2 border-transparent px-1 pb-3 pt-2 text-text-muted hover:text-text data-[state=active]:border-accent data-[state=active]:text-accent font-semibold',
      className
    )}
    {...props}
  />
));
TabsTrigger.displayName = TabsPrimitive.Trigger.displayName;

export const TabsContent = React.forwardRef<
  React.ElementRef<typeof TabsPrimitive.Content>,
  React.ComponentPropsWithoutRef<typeof TabsPrimitive.Content>
>(({ className, ...props }, ref) => (
  <TabsPrimitive.Content
    ref={ref}
    className={cn(
      'mt-3.5 outline-none focus-visible:ring-2 focus-visible:ring-accent animate-in fade-in-50 duration-fast',
      className
    )}
    {...props}
  />
));
TabsContent.displayName = TabsPrimitive.Content.displayName;
