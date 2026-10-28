'use client';

import React, { useCallback, useEffect, useState } from 'react';
import useEmblaCarousel from 'embla-carousel-react';
import { ChevronLeft, ChevronRight, Pause, Play } from 'lucide-react';
import { cn } from '../utils';

export interface CarouselProps {
  children: React.ReactNode[];
  autoplay?: boolean;
  autoplayInterval?: number;
  showDots?: boolean;
  showArrows?: boolean;
  className?: string;
}

export const Carousel: React.FC<CarouselProps> = ({
  children,
  autoplay = false,
  autoplayInterval = 5000,
  showDots = true,
  showArrows = true,
  className,
}) => {
  const [emblaRef, emblaApi] = useEmblaCarousel({ loop: true });
  const [selectedIndex, setSelectedIndex] = useState(0);
  const [scrollSnaps, setScrollSnaps] = useState<number[]>([]);
  const [isPlaying, setIsPlaying] = useState(autoplay);

  const scrollPrev = useCallback(() => emblaApi && emblaApi.scrollPrev(), [emblaApi]);
  const scrollNext = useCallback(() => emblaApi && emblaApi.scrollNext(), [emblaApi]);
  const scrollTo = useCallback((index: number) => emblaApi && emblaApi.scrollTo(index), [emblaApi]);

  const onSelect = useCallback(() => {
    if (!emblaApi) return;
    setSelectedIndex(emblaApi.selectedScrollSnap());
  }, [emblaApi]);

  useEffect(() => {
    if (!emblaApi) return;
    onSelect();
    setScrollSnaps(emblaApi.scrollSnapList());
    emblaApi.on('select', onSelect);
    emblaApi.on('reInit', onSelect);

    return () => {
      emblaApi.off('select', onSelect);
      emblaApi.off('reInit', onSelect);
    };
  }, [emblaApi, onSelect]);

  // Autoplay handler with pause on hover/focus
  useEffect(() => {
    if (!emblaApi || !isPlaying) return;

    const timer = setInterval(() => {
      emblaApi.scrollNext();
    }, autoplayInterval);

    return () => clearInterval(timer);
  }, [emblaApi, isPlaying, autoplayInterval]);

  return (
    <div
      className={cn('relative w-full group overflow-hidden select-none', className)}
      onMouseEnter={() => autoplay && setIsPlaying(false)}
      onMouseLeave={() => autoplay && setIsPlaying(true)}
      onFocus={() => autoplay && setIsPlaying(false)}
      onBlur={() => autoplay && setIsPlaying(true)}
    >
      <div className="overflow-hidden rounded-xl" ref={emblaRef}>
        <div className="flex touch-pan-y">{children}</div>
      </div>

      {/* Prev / Next Arrows */}
      {showArrows && (
        <>
          <button
            type="button"
            onClick={scrollPrev}
            className="absolute left-3 top-1/2 -translate-y-1/2 flex h-10 w-10 items-center justify-center rounded-full bg-surface/80 backdrop-blur-md border border-surface-border text-text shadow-md opacity-0 group-hover:opacity-100 transition-opacity hover:bg-surface hover:text-accent focus:opacity-100 outline-none"
            aria-label="Previous slide"
          >
            <ChevronLeft className="h-5 w-5" />
          </button>
          <button
            type="button"
            onClick={scrollNext}
            className="absolute right-3 top-1/2 -translate-y-1/2 flex h-10 w-10 items-center justify-center rounded-full bg-surface/80 backdrop-blur-md border border-surface-border text-text shadow-md opacity-0 group-hover:opacity-100 transition-opacity hover:bg-surface hover:text-accent focus:opacity-100 outline-none"
            aria-label="Next slide"
          >
            <ChevronRight className="h-5 w-5" />
          </button>
        </>
      )}

      {/* Dots and Autoplay Toggle */}
      <div className="absolute bottom-3 inset-x-0 flex items-center justify-center gap-2">
        {showDots && (
          <div className="flex items-center gap-1.5 px-2.5 py-1 rounded-full bg-bg/70 backdrop-blur-md border border-surface-border">
            {scrollSnaps.map((_, idx) => (
              <button
                key={idx}
                type="button"
                onClick={() => scrollTo(idx)}
                className={cn(
                  'h-2 rounded-full transition-all duration-fast',
                  idx === selectedIndex ? 'w-6 bg-accent' : 'w-2 bg-text-subtle/50 hover:bg-text-subtle'
                )}
                aria-label={`Go to slide ${idx + 1}`}
              />
            ))}

            {autoplay && (
              <button
                type="button"
                onClick={() => setIsPlaying(!isPlaying)}
                className="ml-1 text-text-subtle hover:text-text p-0.5 rounded-full"
                aria-label={isPlaying ? 'Pause slideshow' : 'Play slideshow'}
              >
                {isPlaying ? <Pause className="h-3 w-3" /> : <Play className="h-3 w-3" />}
              </button>
            )}
          </div>
        )}
      </div>
    </div>
  );
};

export const CarouselSlide: React.FC<React.HTMLAttributes<HTMLDivElement>> = ({
  className,
  children,
  ...props
}) => (
  <div className={cn('flex-[0_0_100%] min-w-0 relative', className)} {...props}>
    {children}
  </div>
);
