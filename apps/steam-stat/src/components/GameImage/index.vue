<script setup lang="ts">
import { steamCapsuleImage, steamHeaderImage } from '@/features/stats/model'

defineOptions({
  name: 'GameImage',
})

const props = withDefaults(defineProps<{
  appId: number
  name?: string
  variant?: 'capsule' | 'header'
}>(), {
  variant: 'capsule',
})

// 素材加载失败（离线 / 无素材）时显示占位，避免破图
const failed = ref(false)
watch(() => props.appId, () => {
  failed.value = false
})

const src = computed(() => props.variant === 'header' ? steamHeaderImage(props.appId) : steamCapsuleImage(props.appId))
const initials = computed(() => (props.name ?? '').trim().slice(0, 2).toUpperCase() || '?')
</script>

<template>
  <div class="rounded bg-muted shrink-0 overflow-hidden" :class="variant === 'header' ? 'aspect-[460/215]' : 'w-16 aspect-[184/69]'">
    <img
      v-if="!failed"
      :src="src"
      :alt="name"
      loading="lazy"
      referrerpolicy="no-referrer"
      class="h-full w-full object-cover"
      @error="failed = true"
    >
    <div v-else class="text-xs text-muted-foreground font-semibold flex h-full w-full items-center justify-center">
      {{ initials }}
    </div>
  </div>
</template>
