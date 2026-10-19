<script setup lang="ts">
defineOptions({
  name: 'AppPageHeader',
})

defineProps<{
  icon: string
  title: string
  description?: string
}>()
</script>

<template>
  <div class="mb-2 ms-2 mt-2 flex flex-wrap gap-4 items-center justify-between">
    <div class="flex gap-3 items-center">
      <FaIcon :name="icon" class="size-8" />
      <div>
        <h3 class="text-2xl font-bold">
          {{ title }}
        </h3>
        <p v-if="description" class="text-sm text-gray-500">
          {{ description }}
        </p>
      </div>
      <slot name="extra" />
    </div>
    <div v-if="$slots.actions" class="flex flex-wrap gap-3 items-center">
      <slot name="actions" />
    </div>
  </div>
</template>
