<script setup lang="ts">
import type { DashboardWidget } from '@/store/modules/stats'
import { useI18n } from 'vue-i18n'
import { WIDGETS } from '@/features/stats/widgets'
import { useStatsStore } from '@/store/modules/stats'
import WidgetGrid from './WidgetGrid.vue'

const { t } = useI18n()
const store = useStatsStore()

const editing = ref(false)
const nameDialog = ref(false)
const nameMode = ref<'create' | 'rename'>('create')
const nameInput = ref('')

function layoutName(layout: { id: string, name: string }) {
  return layout.name || t('stats.dashboard.defaultName')
}

const layoutOptions = computed(() => store.layouts.map(layout => ({ value: layout.id, label: layoutName(layout) })))

const available = computed(() => WIDGETS.filter(w => !store.activeLayout.widgets.some(item => item.id === w.id)))

function setWidgets(widgets: DashboardWidget[]) {
  store.activeLayout.widgets = widgets
}

function addWidget(id: DashboardWidget['id'], span: 1 | 2) {
  store.activeLayout.widgets = [...store.activeLayout.widgets, { id, span }]
}

function openName(mode: 'create' | 'rename') {
  nameMode.value = mode
  nameInput.value = mode === 'rename' ? store.activeLayout.name : ''
  nameDialog.value = true
}

function confirmName() {
  const name = nameInput.value.trim() || t('stats.dashboard.untitled')
  if (nameMode.value === 'create') {
    store.addLayout(name, store.activeLayout)
    editing.value = true
  }
  else {
    store.activeLayout.name = name
  }
  nameDialog.value = false
}

function removeLayout() {
  useFaModal().confirm({
    title: t('common.confirm'),
    content: t('stats.dashboard.deleteConfirm', { name: layoutName(store.activeLayout) }),
    onConfirm: () => store.removeLayout(store.activeLayoutId),
  })
}
</script>

<template>
  <div>
    <div class="mb-3 flex flex-wrap gap-2 items-center">
      <FaSelect v-model="store.activeLayoutId" :options="layoutOptions" class="w-48" />
      <FaButton :variant="editing ? 'default' : 'outline'" @click="editing = !editing">
        <FaIcon :name="editing ? 'i-mdi:check' : 'i-mdi:pencil-outline'" />
        {{ editing ? t('stats.dashboard.done') : t('stats.dashboard.edit') }}
      </FaButton>
      <FaButton variant="outline" @click="openName('create')">
        <FaIcon name="i-mdi:plus" />
        {{ t('stats.dashboard.newLayout') }}
      </FaButton>
      <template v-if="editing">
        <FaButton variant="ghost" @click="openName('rename')">
          <FaIcon name="i-mdi:rename-outline" />
          {{ t('stats.dashboard.rename') }}
        </FaButton>
        <FaButton variant="ghost" @click="store.resetLayout(store.activeLayoutId)">
          <FaIcon name="i-mdi:restore" />
          {{ t('stats.dashboard.reset') }}
        </FaButton>
        <FaButton variant="ghost" :disabled="store.layouts.length <= 1" @click="removeLayout">
          <FaIcon name="i-mdi:delete-outline" />
          {{ t('stats.dashboard.delete') }}
        </FaButton>
      </template>
    </div>

    <div v-if="editing && available.length" class="mb-4 p-3 border rounded-lg border-dashed">
      <div class="text-xs text-muted-foreground mb-2">
        {{ t('stats.dashboard.addHint') }}
      </div>
      <div class="flex flex-wrap gap-2">
        <FaButton v-for="widget in available" :key="widget.id" variant="outline" @click="addWidget(widget.id, widget.defaultSpan)">
          <FaIcon :name="widget.icon" />
          {{ t(`stats.widget.${widget.id}`, { n: store.topN }) }}
        </FaButton>
      </div>
    </div>

    <WidgetGrid :widgets="store.activeLayout.widgets" :editable="editing" @update:widgets="setWidgets" />
    <div v-if="!store.activeLayout.widgets.length" class="text-sm text-muted-foreground py-16 text-center">
      {{ t('stats.dashboard.emptyLayout') }}
    </div>

    <FaModal
      v-model="nameDialog"
      :title="nameMode === 'create' ? t('stats.dashboard.newLayout') : t('stats.dashboard.rename')"
      :show-cancel-button="true"
      @confirm="confirmName"
    >
      <FaInput v-model="nameInput" :placeholder="t('stats.dashboard.namePlaceholder')" class="w-full" maxlength="32" />
    </FaModal>
  </div>
</template>
