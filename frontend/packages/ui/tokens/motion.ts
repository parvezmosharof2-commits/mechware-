export const motionTokens = {
  duration: {
    fast: 0.12, // 120ms
    base: 0.2, // 200ms
    slow: 0.32, // 320ms
  },
  easing: {
    standard: [0.2, 0.8, 0.2, 1] as const,
    exit: [0.4, 0, 1, 1] as const,
  },
};

export const motionVariants = {
  fadeIn: {
    hidden: { opacity: 0 },
    visible: {
      opacity: 1,
      transition: { duration: motionTokens.duration.base, ease: motionTokens.easing.standard },
    },
    exit: {
      opacity: 0,
      transition: { duration: motionTokens.duration.fast, ease: motionTokens.easing.exit },
    },
  },
  scaleUp: {
    hidden: { opacity: 0, scale: 0.96 },
    visible: {
      opacity: 1,
      scale: 1,
      transition: { duration: motionTokens.duration.base, ease: motionTokens.easing.standard },
    },
    exit: {
      opacity: 0,
      scale: 0.96,
      transition: { duration: motionTokens.duration.fast, ease: motionTokens.easing.exit },
    },
  },
  slideUp: {
    hidden: { opacity: 0, y: 16 },
    visible: {
      opacity: 1,
      y: 0,
      transition: { duration: motionTokens.duration.base, ease: motionTokens.easing.standard },
    },
    exit: {
      opacity: 0,
      y: 16,
      transition: { duration: motionTokens.duration.fast, ease: motionTokens.easing.exit },
    },
  },
  slideDown: {
    hidden: { opacity: 0, y: -16 },
    visible: {
      opacity: 1,
      y: 0,
      transition: { duration: motionTokens.duration.base, ease: motionTokens.easing.standard },
    },
    exit: {
      opacity: 0,
      y: -16,
      transition: { duration: motionTokens.duration.fast, ease: motionTokens.easing.exit },
    },
  },
  slideRight: {
    hidden: { opacity: 0, x: -24 },
    visible: {
      opacity: 1,
      x: 0,
      transition: { duration: motionTokens.duration.slow, ease: motionTokens.easing.standard },
    },
    exit: {
      opacity: 0,
      x: -24,
      transition: { duration: motionTokens.duration.fast, ease: motionTokens.easing.exit },
    },
  },
  drawerRight: {
    hidden: { x: '100%' },
    visible: {
      x: 0,
      transition: { duration: motionTokens.duration.slow, ease: motionTokens.easing.standard },
    },
    exit: {
      x: '100%',
      transition: { duration: motionTokens.duration.base, ease: motionTokens.easing.exit },
    },
  },
  drawerBottom: {
    hidden: { y: '100%' },
    visible: {
      y: 0,
      transition: { duration: motionTokens.duration.slow, ease: motionTokens.easing.standard },
    },
    exit: {
      y: '100%',
      transition: { duration: motionTokens.duration.base, ease: motionTokens.easing.exit },
    },
  },
  badgeBounce: {
    initial: { scale: 1 },
    bounce: {
      scale: [1, 1.25, 0.95, 1.1, 1],
      transition: { duration: 0.45, ease: 'easeInOut' },
    },
  },
};
