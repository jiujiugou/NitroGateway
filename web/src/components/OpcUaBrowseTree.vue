<template>
  <el-dialog v-model="open" title="OPC UA 节点浏览" width="560px">
    <el-tree
      v-loading="loading"
      lazy
      node-key="nodeId"
      highlight-current
      :load="loadNode"
      :props="{ label: 'name', isLeaf: 'isLeaf' }"
      style="max-height:480px;overflow:auto"
      @node-click="onNodeClick"
    />
    <div class="hint">点击变量节点（叶子）自动回填地址、类型与权限。</div>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { browseNodes } from '../api/devices'

// ADR-070 层次1：OPC UA 节点浏览树（懒加载，点选 Variable 叶子回填地址/类型/权限）。
// 自 PointList 抽出，供 OPC UA 点位页复用；父组件 v-model 控制显隐、pick 接收回填结果。
const props = defineProps<{
  modelValue: boolean
  deviceId: string
}>()

const emit = defineEmits<{
  (e: 'update:modelValue', v: boolean): void
  (e: 'pick', node: { nodeId: string; typeName: string; access: string }): void
}>()

const open = computed({
  get: () => props.modelValue,
  set: (v: boolean) => emit('update:modelValue', v)
})
const loading = ref(false)

// ADR-070 层次1：懒加载某一层子节点。根（level 0）parent 缺省 = Objects 目录。
async function loadNode(node: any, resolve: (data: any[]) => void) {
  loading.value = true
  try {
    const parent = node.level === 0 ? '' : node.data.nodeId
    const list = await browseNodes(props.deviceId, parent)
    // isLeaf 决定树是否显示展开箭头：仅变量节点是叶子
    resolve(list.map(n => ({ ...n, isLeaf: n.isVariable })))
  } catch (err: any) {
    ElMessage.error(`浏览失败: ${err?.response?.data?.error?.message ?? err?.message ?? '未知错误'}`)
    resolve([])
  } finally {
    loading.value = false
  }
}

// ADR-070 层次1 D6：点选变量叶子回填 Address/DataType/Access（由父组件处理回填）。
function onNodeClick(node: any) {
  if (!node.isVariable) return
  emit('pick', { nodeId: node.nodeId, typeName: node.typeName, access: node.access })
  open.value = false
}
</script>

<style scoped>
.hint { font-size:12px; color:var(--text-dim,#909399); margin-top:8px; }
</style>
