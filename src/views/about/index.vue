<script setup lang="ts">
import MarkdownIt from 'markdown-it'
import taskLists from 'markdown-it-task-lists'
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import aboutMd from './about.md?raw'

const electronApi = (window as any).electron
const htmlContent = ref('')
const loading = ref(true)
const markdownContainer = ref<HTMLElement | null>(null)

// 初始化 markdown-it
const md = new MarkdownIt({
  html: true,
  linkify: true,
  typographer: true,
  breaks: true,
})

// 使用任务列表插件
md.use(taskLists, {
  enabled: true,
  label: true,
  labelAfter: false,
})

// 处理链接点击事件
function handleLinkClick(event: MouseEvent) {
  const target = event.target as HTMLElement
  // 向上查找最近的 a 标签（支持点击链接内的图片等嵌套元素）
  const link = target.closest('a')
  if (link) {
    const href = link.getAttribute('href')
    if (href && (href.startsWith('http://') || href.startsWith('https://'))) {
      event.preventDefault()
      // 使用 Electron 的 shell API 在默认浏览器中打开链接
      electronApi.shellOpenExternal(href)
    }
  }
}

// 加载并渲染 markdown
async function loadReadme() {
  loading.value = true
  try {
    htmlContent.value = md.render(aboutMd)
  }
  catch (error: any) {
    console.error('渲染 Markdown 失败:', error)
    htmlContent.value = '<div class="error-message"><p>渲染出错，请重试</p></div>'
  }
  finally {
    loading.value = false
    // 等待 DOM 更新后添加事件监听器
    await nextTick()
    if (markdownContainer.value) {
      markdownContainer.value.addEventListener('click', handleLinkClick)
    }
  }
}

onMounted(() => {
  loadReadme()
})

onBeforeUnmount(() => {
  // 清理事件监听器
  if (markdownContainer.value) {
    markdownContainer.value.removeEventListener('click', handleLinkClick)
  }
})
</script>

<template>
  <div class="about-container">
    <FaPageHeader title="关于 Steam Stat" />

    <FaPageMain>
      <div v-if="loading" class="loading-container">
        <el-skeleton :rows="20" animated />
      </div>

      <div v-else ref="markdownContainer" v-motion-fade class="markdown-container">
        <div class="markdown-body" v-html="htmlContent" />
      </div>
    </FaPageMain>
  </div>
</template>

<style scoped>
.about-container {
  display: flex;
  flex-direction: column;
  height: 100%;
}

.loading-container {
  padding: 20px;
}

.markdown-container {
  padding: 20px;
  animation: fade-in 0.5s ease-in-out;
}

@keyframes fade-in {
  from {
    opacity: 0;
    transform: translateY(10px);
  }

  to {
    opacity: 1;
    transform: translateY(0);
  }
}

.error-message {
  padding: 40px;
  color: #f56c6c;
  text-align: center;
}

/* Markdown 样式 */
.markdown-body {
  font-size: 16px;
  line-height: 1.8;
  color: #303133;
  overflow-wrap: break-word;
}

.markdown-body :deep(h1) {
  padding-bottom: 10px;
  margin-top: 24px;
  margin-bottom: 16px;
  font-size: 2.5em;
  font-weight: 700;
  color: #409eff;
  border-bottom: 2px solid #dcdfe6;
}

.markdown-body :deep(h2) {
  padding-bottom: 8px;
  margin-top: 24px;
  margin-bottom: 16px;
  font-size: 2em;
  font-weight: 600;
  border-bottom: 1px solid #e4e7ed;
}

.markdown-body :deep(h3) {
  margin-top: 20px;
  margin-bottom: 12px;
  font-size: 1.5em;
  font-weight: 600;
}

.markdown-body :deep(h4) {
  margin-top: 16px;
  margin-bottom: 10px;
  font-size: 1.25em;
  font-weight: 600;
}

.markdown-body :deep(p) {
  margin-top: 0;
  margin-bottom: 16px;
}

.markdown-body :deep(a) {
  color: #409eff;
  text-decoration: underline;
  transition: all 0.3s ease;
}

.markdown-body :deep(a:hover) {
  color: #79bbff;
}

.markdown-body :deep(ul),
.markdown-body :deep(ol) {
  padding-left: 2em;
  margin-top: 0;
  margin-bottom: 16px;
}

.markdown-body :deep(ul) {
  list-style-type: disc;
}

.markdown-body :deep(ol) {
  list-style-type: decimal;
}

.markdown-body :deep(li) {
  margin-top: 4px;
  margin-bottom: 4px;
}

.markdown-body :deep(li > p) {
  margin-bottom: 8px;
}

.markdown-body :deep(code) {
  padding: 2px 6px;
  margin: 0;
  font-family: Consolas, Monaco, "Courier New", monospace;
  font-size: 0.9em;
  background-color: #f5f7fa;
  border-radius: 4px;
}

.markdown-body :deep(pre) {
  padding: 16px;
  margin-bottom: 16px;
  overflow: auto;
  font-size: 0.9em;
  line-height: 1.6;
  background-color: #f0f2f5;
  border-radius: 8px;
}

.markdown-body :deep(pre code) {
  display: block;
  padding: 0;
  margin: 0;
  background-color: transparent;
  border-radius: 0;
}

.markdown-body :deep(blockquote) {
  padding: 0 1em;
  margin: 0;
  margin-bottom: 16px;
  color: #909399;
  border-left: 4px solid #dcdfe6;
}

.markdown-body :deep(table) {
  display: block;
  width: 100%;
  margin-bottom: 16px;
  overflow: auto;
  border-collapse: collapse;
}

.markdown-body :deep(table th),
.markdown-body :deep(table td) {
  padding: 8px 13px;
  border: 1px solid #dcdfe6;
}

.markdown-body :deep(table th) {
  font-weight: 600;
  background-color: #f5f7fa;
}

.markdown-body :deep(table tr:nth-child(even)) {
  background-color: #fafafa;
}

.markdown-body :deep(img) {
  max-width: 100%;
  height: auto;
  margin: 16px 0;
  border-radius: 8px;
  box-shadow: 0 2px 12px rgb(0 0 0 / 10%);
  transition: transform 0.3s ease;
}

.markdown-body :deep(img:hover) {
  transform: scale(1.02);
}

.markdown-body :deep(hr) {
  height: 2px;
  padding: 0;
  margin: 24px 0;
  background-color: #dcdfe6;
  border: 0;
}

/* Badge 样式 */
.markdown-body :deep(img[src*="shields.io"]),
.markdown-body :deep(img[src*="badge"]) {
  display: inline-block;
  margin: 4px;
  box-shadow: none;
}

.markdown-body :deep(img[src*="shields.io"]:hover),
.markdown-body :deep(img[src*="badge"]:hover) {
  transform: none;
}

/* 强调样式 */
.markdown-body :deep(strong) {
  font-weight: 600;
  color: #303133;
}

.markdown-body :deep(em) {
  font-style: italic;
}

/* 删除线 */
.markdown-body :deep(del) {
  text-decoration: line-through;
}

/* 任务列表 */
.markdown-body :deep(.task-list-item) {
  list-style-type: none;
}

.markdown-body :deep(.task-list-item input[type="checkbox"]) {
  margin-right: 8px;
  margin-left: -1.5em;
  accent-color: #409eff;
  pointer-events: none;
  cursor: default;
}

.markdown-body :deep(.task-list-item input[type="checkbox"]:checked) {
  accent-color: #67c23a;
}

.markdown-body :deep(.task-list-item.checked) {
  opacity: 0.7;
}

.markdown-body :deep(.task-list-item.checked > p) {
  color: #909399;
  text-decoration: line-through;
}

/* 响应式设计 */
@media (width <= 768px) {
  .markdown-container {
    padding: 12px;
  }

  .markdown-body {
    font-size: 14px;
  }

  .markdown-body :deep(h1) {
    font-size: 2em;
  }

  .markdown-body :deep(h2) {
    font-size: 1.5em;
  }

  .markdown-body :deep(h3) {
    font-size: 1.25em;
  }
}

/* 滚动条美化 */
.markdown-container::-webkit-scrollbar {
  width: 8px;
}

.markdown-container::-webkit-scrollbar-track {
  background: #fafafa;
  border-radius: 4px;
}

.markdown-container::-webkit-scrollbar-thumb {
  background: #d4d7de;
  border-radius: 4px;
  transition: background 0.3s ease;
}

.markdown-container::-webkit-scrollbar-thumb:hover {
  background: #a0cfff;
}
</style>
