'use client';

import React, { useState } from 'react';
import { Maximize2, X, ChevronLeft, ChevronRight } from 'lucide-react';
import { cn } from '../utils';

export interface GalleryImage {
  src: string;
  alt: string;
  thumbnail?: string;
}

export interface ImageGalleryProps {
  images: GalleryImage[];
  className?: string;
}

export const ImageGallery: React.FC<ImageGalleryProps> = ({ images, className }) => {
  const [selectedIndex, setSelectedIndex] = useState(0);
  const [isLightboxOpen, setIsLightboxOpen] = useState(false);
  const [zoomStyle, setZoomStyle] = useState<React.CSSProperties>({ display: 'none' });

  if (!images || images.length === 0) return null;
  const currentImage = images[selectedIndex] || images[0];

  const handleMouseMove = (e: React.MouseEvent<HTMLDivElement>) => {
    const { left, top, width, height } = e.currentTarget.getBoundingClientRect();
    const x = ((e.clientX - left) / width) * 100;
    const y = ((e.clientY - top) / height) * 100;

    setZoomStyle({
      display: 'block',
      backgroundImage: `url(${currentImage.src})`,
      backgroundPosition: `${x}% ${y}%`,
      backgroundSize: '250%',
    });
  };

  const handleMouseLeave = () => {
    setZoomStyle({ display: 'none' });
  };

  const handlePrev = (e?: React.MouseEvent) => {
    e?.stopPropagation();
    setSelectedIndex((prev) => (prev > 0 ? prev - 1 : images.length - 1));
  };

  const handleNext = (e?: React.MouseEvent) => {
    e?.stopPropagation();
    setSelectedIndex((prev) => (prev < images.length - 1 ? prev + 1 : 0));
  };

  return (
    <div className={cn('flex flex-col-reverse md:flex-row gap-4 font-sans select-none', className)}>
      {/* Thumbnail Rail */}
      <div className="flex md:flex-col gap-2.5 overflow-x-auto md:overflow-y-auto max-h-[500px] shrink-0 scrollbar-thin">
        {images.map((img, idx) => (
          <button
            key={idx}
            type="button"
            onClick={() => setSelectedIndex(idx)}
            className={cn(
              'relative h-16 w-16 md:h-20 md:w-20 rounded-md overflow-hidden border-2 transition-all p-1 bg-surface shrink-0 outline-none cursor-pointer',
              idx === selectedIndex
                ? 'border-accent shadow-glow-cyan-sm'
                : 'border-surface-border hover:border-surface-border-strong opacity-75 hover:opacity-100'
            )}
            aria-label={`View image ${idx + 1}: ${img.alt}`}
          >
            {/* eslint-disable-next-line @next/next/no-img-element */}
            <img
              src={img.thumbnail || img.src}
              alt={img.alt}
              className="h-full w-full object-contain"
            />
          </button>
        ))}
      </div>

      {/* Main Image Stage */}
      <div
        className="relative flex-1 aspect-square rounded-xl border border-surface-border bg-surface flex items-center justify-center overflow-hidden p-6 group cursor-crosshair"
        onMouseMove={handleMouseMove}
        onMouseLeave={handleMouseLeave}
      >
        {/* eslint-disable-next-line @next/next/no-img-element */}
        <img
          src={currentImage.src}
          alt={currentImage.alt}
          className="max-h-full max-w-full object-contain transition-opacity duration-fast"
        />

        {/* Hover Zoom Lens Layer */}
        <div
          className="pointer-events-none absolute inset-0 z-10 bg-no-repeat rounded-xl shadow-inner border border-accent/40"
          style={zoomStyle}
        />

        {/* Lightbox Trigger Button */}
        <button
          type="button"
          onClick={() => setIsLightboxOpen(true)}
          className="absolute top-3 right-3 p-2 rounded-full bg-surface/80 backdrop-blur-md border border-surface-border text-text hover:text-accent hover:border-accent shadow-sm opacity-0 group-hover:opacity-100 transition-opacity outline-none cursor-pointer"
          aria-label="Open fullscreen image view"
        >
          <Maximize2 className="h-4 w-4" />
        </button>
      </div>

      {/* Fullscreen Lightbox Modal */}
      {isLightboxOpen && (
        <div
          className="fixed inset-0 z-modal bg-black/90 backdrop-blur-md flex items-center justify-center p-4 animate-in fade-in-50 duration-fast"
          onClick={() => setIsLightboxOpen(false)}
        >
          <button
            type="button"
            onClick={() => setIsLightboxOpen(false)}
            className="absolute top-4 right-4 p-2.5 rounded-full bg-surface/70 text-text hover:text-accent border border-surface-border outline-none cursor-pointer"
            aria-label="Close fullscreen view"
          >
            <X className="h-6 w-6" />
          </button>

          <button
            type="button"
            onClick={handlePrev}
            className="absolute left-4 top-1/2 -translate-y-1/2 p-3 rounded-full bg-surface/70 text-text hover:text-accent border border-surface-border outline-none cursor-pointer"
            aria-label="Previous image"
          >
            <ChevronLeft className="h-6 w-6" />
          </button>

          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img
            src={currentImage.src}
            alt={currentImage.alt}
            className="max-h-[85vh] max-w-[85vw] object-contain select-none"
            onClick={(e) => e.stopPropagation()}
          />

          <button
            type="button"
            onClick={handleNext}
            className="absolute right-4 top-1/2 -translate-y-1/2 p-3 rounded-full bg-surface/70 text-text hover:text-accent border border-surface-border outline-none cursor-pointer"
            aria-label="Next image"
          >
            <ChevronRight className="h-6 w-6" />
          </button>
        </div>
      )}
    </div>
  );
};
