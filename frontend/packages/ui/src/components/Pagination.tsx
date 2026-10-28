'use client';

import React from 'react';
import { ChevronLeft, ChevronRight, MoreHorizontal } from 'lucide-react';
import { cn } from '../utils';

export const Pagination = ({ className, ...props }: React.ComponentProps<'nav'>) => (
  <nav
    role="navigation"
    aria-label="pagination"
    className={cn('mx-auto flex w-full justify-center font-sans', className)}
    {...props}
  />
);
Pagination.displayName = 'Pagination';

export const PaginationContent = React.forwardRef<
  HTMLUListElement,
  React.ComponentProps<'ul'>
>(({ className, ...props }, ref) => (
  <ul
    ref={ref}
    className={cn('flex flex-row items-center gap-1.5', className)}
    {...props}
  />
));
PaginationContent.displayName = 'PaginationContent';

export const PaginationItem = React.forwardRef<
  HTMLLIElement,
  React.ComponentProps<'li'>
>(({ className, ...props }, ref) => (
  <li ref={ref} className={cn('', className)} {...props} />
));
PaginationItem.displayName = 'PaginationItem';

type PaginationLinkProps = {
  isActive?: boolean;
} & React.ComponentProps<'button'>;

export const PaginationLink = ({
  className,
  isActive,
  ...props
}: PaginationLinkProps) => (
  <button
    aria-current={isActive ? 'page' : undefined}
    className={cn(
      'inline-flex items-center justify-center rounded-md text-sm font-medium transition-colors h-10 w-10 outline-none cursor-pointer',
      isActive
        ? 'bg-accent text-accent-fg font-bold shadow-glow-cyan-sm'
        : 'text-text-muted hover:bg-bg-muted hover:text-text border border-surface-border',
      className
    )}
    {...props}
  />
);
PaginationLink.displayName = 'PaginationLink';

export const PaginationPrevious = ({
  className,
  ...props
}: React.ComponentProps<typeof PaginationLink>) => (
  <button
    aria-label="Go to previous page"
    className={cn(
      'inline-flex items-center justify-center gap-1 px-3 h-10 rounded-md text-sm font-medium text-text-muted hover:bg-bg-muted hover:text-text border border-surface-border transition-colors disabled:opacity-40 disabled:pointer-events-none cursor-pointer',
      className
    )}
    {...props}
  >
    <ChevronLeft className="h-4 w-4" />
    <span>Prev</span>
  </button>
);
PaginationPrevious.displayName = 'PaginationPrevious';

export const PaginationNext = ({
  className,
  ...props
}: React.ComponentProps<typeof PaginationLink>) => (
  <button
    aria-label="Go to next page"
    className={cn(
      'inline-flex items-center justify-center gap-1 px-3 h-10 rounded-md text-sm font-medium text-text-muted hover:bg-bg-muted hover:text-text border border-surface-border transition-colors disabled:opacity-40 disabled:pointer-events-none cursor-pointer',
      className
    )}
    {...props}
  >
    <span>Next</span>
    <ChevronRight className="h-4 w-4" />
  </button>
);
PaginationNext.displayName = 'PaginationNext';

export const PaginationEllipsis = ({
  className,
  ...props
}: React.ComponentProps<'span'>) => (
  <span
    aria-hidden
    className={cn('flex h-10 w-10 items-center justify-center text-text-subtle', className)}
    {...props}
  >
    <MoreHorizontal className="h-4 w-4" />
    <span className="sr-only">More pages</span>
  </span>
);
PaginationEllipsis.displayName = 'PaginationEllipsis';
