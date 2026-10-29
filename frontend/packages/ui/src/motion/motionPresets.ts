'use client';

import { Variants } from 'framer-motion';
import { motionTokens } from '../../tokens/motion';

/**
 * Standard motion presets that respect prefers-reduced-motion
 */
export const fadePreset: Variants = {
  hidden: { opacity: 0 },
  visible: {
    opacity: 1,
    transition: {
      duration: motionTokens.duration.base,
      ease: motionTokens.easing.standard,
    },
  },
  exit: {
    opacity: 0,
    transition: {
      duration: motionTokens.duration.fast,
      ease: motionTokens.easing.exit,
    },
  },
};

export const scalePreset: Variants = {
  hidden: { opacity: 0, scale: 0.96 },
  visible: {
    opacity: 1,
    scale: 1,
    transition: {
      duration: motionTokens.duration.base,
      ease: motionTokens.easing.standard,
    },
  },
  exit: {
    opacity: 0,
    scale: 0.96,
    transition: {
      duration: motionTokens.duration.fast,
      ease: motionTokens.easing.exit,
    },
  },
};

export const slideUpPreset: Variants = {
  hidden: { opacity: 0, y: 16 },
  visible: {
    opacity: 1,
    y: 0,
    transition: {
      duration: motionTokens.duration.base,
      ease: motionTokens.easing.standard,
    },
  },
  exit: {
    opacity: 0,
    y: 16,
    transition: {
      duration: motionTokens.duration.fast,
      ease: motionTokens.easing.exit,
    },
  },
};

export const cartBadgeBouncePreset: Variants = {
  initial: { scale: 1 },
  bounce: {
    scale: [1, 1.3, 0.9, 1.15, 1],
    transition: { duration: 0.45, ease: 'easeInOut' },
  },
};

/**
 * Returns variants adapted for prefers-reduced-motion: removes translations/scale and shortens transitions
 */
export function getMotionVariants(variants: Variants, prefersReducedMotion = false): Variants {
  if (!prefersReducedMotion) return variants;

  const reduced: Variants = {};
  for (const [key, variant] of Object.entries(variants)) {
    if (typeof variant === 'object' && variant !== null) {
      reduced[key] = {
        ...variant,
        x: 0,
        y: 0,
        scale: 1,
        transition: { duration: 0.01 },
      };
    } else {
      reduced[key] = variant;
    }
  }
  return reduced;
}

