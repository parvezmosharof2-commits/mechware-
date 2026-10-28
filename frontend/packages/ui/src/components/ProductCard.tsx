'use client';

import React, { useState } from 'react';
import { Heart, ShoppingCart, Check, Zap } from 'lucide-react';
import { cn } from '../utils';
import { Price } from './Price';
import { Rating } from './Rating';
import { Badge } from './Badge';

export interface ProductCardProps {
  id: string;
  title: string;
  brand: string;
  primaryImage: string;
  secondaryImage?: string;
  price: number;
  compareAtPrice?: number;
  currency?: 'BDT' | 'USD';
  rating?: number;
  reviewCount?: number;
  deliveryHint?: string;
  badges?: Array<{
    label: string;
    variant: 'deal' | 'new' | 'bestseller' | 'authentic' | 'low-stock' | 'free-delivery' | 'ai';
  }>;
  size?: 'default' | 'compact' | 'horizontal';
  isWishlisted?: boolean;
  onWishlistToggle?: (id: string, isFav: boolean) => void;
  onAddToCart?: (id: string) => void;
  href?: string;
  className?: string;
}

export const ProductCard: React.FC<ProductCardProps> = ({
  id,
  title,
  brand,
  primaryImage,
  secondaryImage,
  price,
  compareAtPrice,
  currency = 'BDT',
  rating = 4.8,
  reviewCount = 124,
  deliveryHint = 'Free delivery in Dhaka',
  badges = [],
  size = 'default',
  isWishlisted: initialWishlist = false,
  onWishlistToggle,
  onAddToCart,
  href = `/product/${id}`,
  className,
}) => {
  const [isWishlisted, setIsWishlisted] = useState(initialWishlist);
  const [isAdded, setIsAdded] = useState(false);

  const handleWishlistClick = (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();
    const next = !isWishlisted;
    setIsWishlisted(next);
    onWishlistToggle?.(id, next);
  };

  const handleAddToCartClick = (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setIsAdded(true);
    onAddToCart?.(id);
    setTimeout(() => {
      setIsAdded(false);
    }, 1200);
  };

  /* ---------------- HORIZONTAL VARIANT ---------------- */
  if (size === 'horizontal') {
    return (
      <div
        className={cn(
          'group relative flex items-center gap-3.5 rounded-lg border border-surface-border bg-surface p-3 font-sans transition-all duration-base hover:border-surface-border-strong hover:shadow-md select-none',
          className
        )}
      >
        <a href={href} className="absolute inset-0 z-0" aria-label={title} />

        {/* Thumbnail Image */}
        <div className="relative h-20 w-20 shrink-0 overflow-hidden rounded-md border border-surface-border bg-bg-muted p-1">
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img
            src={primaryImage}
            alt={title}
            className="h-full w-full object-contain transition-transform duration-base group-hover:scale-105"
          />
        </div>

        {/* Content */}
        <div className="flex-1 min-w-0 flex flex-col justify-between z-10 pointer-events-none">
          <span className="text-xs text-text-subtle uppercase tracking-wider font-semibold truncate">
            {brand}
          </span>
          <h4 className="text-sm font-semibold text-text truncate group-hover:text-accent transition-colors">
            {title}
          </h4>
          <Price amount={price} compareAt={compareAtPrice} currency={currency} size="sm" />
        </div>

        {/* Action Button */}
        <div className="z-10 shrink-0">
          <button
            type="button"
            onClick={handleAddToCartClick}
            className={cn(
              'flex h-9 w-9 items-center justify-center rounded-md border text-sm transition-all outline-none cursor-pointer',
              isAdded
                ? 'bg-status-online text-bg border-status-online'
                : 'border-surface-border bg-bg-muted text-text hover:border-accent hover:text-accent'
            )}
            aria-label={`Add ${title} to cart`}
          >
            {isAdded ? <Check className="h-4 w-4 stroke-[3]" /> : <ShoppingCart className="h-4 w-4" />}
          </button>
        </div>
      </div>
    );
  }

  /* ---------------- COMPACT VARIANT ---------------- */
  if (size === 'compact') {
    return (
      <div
        className={cn(
          'group relative flex flex-col rounded-lg border border-surface-border bg-surface p-3 font-sans transition-all duration-base hover:-translate-y-1 hover:border-surface-border-strong hover:shadow-md select-none w-52 shrink-0',
          className
        )}
      >
        <a href={href} className="absolute inset-0 z-0" aria-label={title} />

        {/* Wishlist Button */}
        <button
          type="button"
          onClick={handleWishlistClick}
          className="absolute top-2 right-2 z-10 p-1.5 rounded-full bg-surface/80 backdrop-blur-md border border-surface-border text-text-muted hover:text-fault transition-colors outline-none cursor-pointer"
          aria-label={isWishlisted ? 'Remove from wishlist' : 'Add to wishlist'}
        >
          <Heart
            className={cn(
              'h-3.5 w-3.5 transition-transform active:scale-125',
              isWishlisted && 'fill-fault stroke-fault'
            )}
          />
        </button>

        {/* Square Image */}
        <div className="relative aspect-square w-full overflow-hidden rounded-md border border-surface-border/50 bg-bg-muted p-2 mb-2.5">
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img
            src={primaryImage}
            alt={title}
            className="h-full w-full object-contain transition-transform duration-base group-hover:scale-105"
          />
        </div>

        {/* Meta */}
        <div className="flex flex-col flex-1 z-10 pointer-events-none">
          <span className="text-[11px] text-text-subtle uppercase tracking-wider font-semibold">
            {brand}
          </span>
          <h4 className="text-xs font-semibold text-text line-clamp-2 mt-0.5 group-hover:text-accent transition-colors leading-snug">
            {title}
          </h4>
          <div className="mt-2">
            <Price amount={price} compareAt={compareAtPrice} currency={currency} size="sm" />
          </div>
        </div>
      </div>
    );
  }

  /* ---------------- DEFAULT GRID VARIANT ---------------- */
  return (
    <div
      className={cn(
        'group relative flex flex-col rounded-xl border border-surface-border bg-surface p-4 font-sans transition-all duration-base hover:-translate-y-1 hover:border-surface-border-strong hover:shadow-lg select-none',
        className
      )}
    >
      {/* Primary Accessible Link for whole card */}
      <a href={href} className="absolute inset-0 z-0" aria-label={title} />

      {/* Top Controls: Badges (Left) & Wishlist (Right) */}
      <div className="absolute top-3 inset-x-3 flex items-start justify-between z-10 pointer-events-none">
        <div className="flex flex-col gap-1 items-start pointer-events-auto">
          {badges.slice(0, 2).map((b, i) => (
            <Badge key={i} variant={b.variant} size="sm">
              {b.label}
            </Badge>
          ))}
        </div>

        <button
          type="button"
          onClick={handleWishlistClick}
          className="pointer-events-auto flex h-8 w-8 items-center justify-center rounded-full bg-surface/80 backdrop-blur-md border border-surface-border text-text-muted hover:text-fault transition-colors outline-none cursor-pointer"
          aria-label={isWishlisted ? 'Remove from wishlist' : 'Add to wishlist'}
        >
          <Heart
            className={cn(
              'h-4 w-4 transition-transform active:scale-125',
              isWishlisted && 'fill-fault stroke-fault text-fault'
            )}
          />
        </button>
      </div>

      {/* Square Image with Hover Crossfade */}
      <div className="relative aspect-square w-full overflow-hidden rounded-lg border border-surface-border/50 bg-bg-muted p-4 mb-3.5">
        {/* Primary Image */}
        {/* eslint-disable-next-line @next/next/no-img-element */}
        <img
          src={primaryImage}
          alt={title}
          className={cn(
            'h-full w-full object-contain transition-all duration-slow',
            secondaryImage ? 'group-hover:opacity-0 group-hover:scale-105' : 'group-hover:scale-105'
          )}
        />

        {/* Secondary Image for smooth crossfade */}
        {secondaryImage && (
          // eslint-disable-next-line @next/next/no-img-element
          <img
            src={secondaryImage}
            alt={`${title} alternate view`}
            className="absolute inset-0 h-full w-full object-contain p-4 opacity-0 transition-all duration-slow group-hover:opacity-100 group-hover:scale-105"
          />
        )}
      </div>

      {/* Product Details */}
      <div className="flex flex-col flex-1 z-10 pointer-events-none">
        <span className="text-xs text-text-subtle uppercase tracking-wider font-semibold">
          {brand}
        </span>

        <h4 className="text-sm font-semibold text-text line-clamp-2 mt-1 leading-snug group-hover:text-accent transition-colors">
          {title}
        </h4>

        {/* Rating */}
        <div className="mt-2">
          <Rating value={rating} count={reviewCount} size="sm" />
        </div>

        {/* Price Block */}
        <div className="mt-2.5">
          <Price
            amount={price}
            compareAt={compareAtPrice}
            currency={currency}
            size="md"
          />
        </div>

        {/* Delivery Hint */}
        {deliveryHint && (
          <div className="mt-2 flex items-center gap-1.5 text-xs text-text-muted">
            <Zap className="h-3.5 w-3.5 text-accent shrink-0" />
            <span className="truncate">{deliveryHint}</span>
          </div>
        )}
      </div>

      {/* Quick Add To Cart Button (hover on desktop, always icon on mobile) */}
      <div className="mt-3.5 z-10 pt-2 border-t border-surface-border/40">
        <button
          type="button"
          onClick={handleAddToCartClick}
          className={cn(
            'w-full flex items-center justify-center gap-2 h-10 rounded-md font-semibold text-sm transition-all duration-fast outline-none select-none cursor-pointer',
            isAdded
              ? 'bg-status-online text-bg shadow-sm'
              : 'bg-accent text-accent-fg hover:bg-accent-hover hover:shadow-glow-cyan active:scale-[0.98]'
          )}
        >
          {isAdded ? (
            <>
              <Check className="h-4 w-4 stroke-[3]" />
              <span>Added to Cart</span>
            </>
          ) : (
            <>
              <ShoppingCart className="h-4 w-4" />
              <span>Add to Cart</span>
            </>
          )}
        </button>
      </div>
    </div>
  );
};
