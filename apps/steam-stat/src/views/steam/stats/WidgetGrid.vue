<script setup lang="ts">
import type { DashboardWidget } from '@/store/modules/stats'
import { useI18n } from 'vue-i18n'
import { widgetComponents } from './widgets'

const props = defineProps<{
  widgets: DashboardWidget[]
  editable?: boolean
}>()

const emit = defineEmits<{
  'update:widgets': [widgets: DashboardWidget[]]
}>()

const { t } = useI18n()
const dragIndex = ref<number | null>(null)
const overIndex = ref<number | null>(null)

function update(list: DashboardWidget[]) {
  emit('update:widgets', list)
}

function move(index: number, delta: number) {
  const target = index + delta
  if (target < 0 || target >= props.widgets.length) {
    return
  }
  const list = [...props.widgets]
  const [item] = list.splice(index, 1)
  list.splice(target, 0, item)
  update(list)
}

function toggleSpan(index: number) {
  update(props.widgets.map((w, i) => i === index ? { ...w, span: w.span === 2 ? 1 : 2 } : w))
}

function remove(index: number) {
  update(props.widgets.filter((_, i) => i !== index))
}

function onDrop(index: number) {
  if (dragIndex.value != null && dragIndex.value !== index) {
    const list = [...props.widgets]
    const [item] = list.splice(dragIndex.value, 1)
    list.splice(index, 0, item)
    update(list)
  }
  dragIndex.value = null
  overIndex.value = null
}
</script>

<template>
  <TransitionGroup tag="div" name="widget" class="gap-4 grid grid-cols-1 xl:grid-cols-2">
    <div
      v-for="(widget, index) in widgets"
      :key="`${widget.id}-${index}`"
      class="min-w-0 relative"
      :class="[
        widget.span === 2 ? 'xl:col-span-2' : '',
        editable ? 'edit-frame rounded-lg' : '',
        overIndex === index && dragIndex !== index ? 'ring-2 ring-primary' : '',
      ]"
      :draggable="editable"
      @dragstart="dragIndex = index"
      @dragover.prevent="overIndex = index"
      @dragleave="overIndex = null"
      @drop.prevent="onDrop(index)"
      @dragend="dragIndex = null; overIndex = null"
    >
      <div v-if="editable" class="p-1 border rounded-md bg-background flex gap-1 shadow right-2 top-2 absolute z-10">
        <FaIcon name="i-mdi:drag" class="text-muted-foreground m-1.5 cursor-grab" />
        <FaTooltip :text="t('stats.dashboard.moveUp')">
          <FaButton size="icon-sm" variant="ghost" :disabled="index === 0" @click="move(index, -1)">
            <FaIcon name="i-mdi:arrow-up" />
          </FaButton>
        </FaTooltip>
        <FaTooltip :text="t('stats.dashboard.moveDown')">
          <FaButton size="icon-sm" variant="ghost" :disabled="index === widgets.length - 1" @click="move(index, 1)">
            <FaIcon name="i-mdi:arrow-down" />
          </FaButton>
        </FaTooltip>
        <FaTooltip :text="widget.span === 2 ? t('stats.dashboard.narrow') : t('stats.dashboard.widen')">
          <FaButton size="icon-sm" variant="ghost" @click="toggleSpan(index)">
            <FaIcon :name="widget.span === 2 ? 'i-mdi:arrow-collapse-horizontal' : 'i-mdi:arrow-expand-horizontal'" />
          </FaButton>
        </FaTooltip>
        <FaTooltip :text="t('stats.dashboard.remove')">
          <FaButton size="icon-sm" variant="ghost" @click="remove(index)">
            <FaIcon name="i-mdi:close" />
          </FaButton>
        </FaTooltip>
      </div>
      <div :class="editable ? 'pointer-events-none select-none opacity-90' : ''">
        <component :is="widgetComponents[widget.id]" />
      </div>
    </div>
  </TransitionGroup>
</template>

<style scoped>
.edit-frame {
  outline: 2px dashed oklch(var(--border));
  outline-offset: 4px;
}

.widget-move,
.widget-enter-active,
.widget-leave-active {
  transition: all 0.3s ease;
}

.widget-enter-from,
.widget-leave-to {
  opacity: 0;
  transform: translateY(12px);
}

.widget-leave-active {
  position: absolute;
}

@media (prefers-reduced-motion: reduce) {
  .widget-move,
  .widget-enter-active,
  .widget-leave-active {
    transition: none;
  }
}
</style>
