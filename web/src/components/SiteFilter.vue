<template>
  <!-- ADR-035 第 1 步 Web 维度：站点下拉。"全部站点"= 不过滤（含未标注站点的旧数据）；具体站点来自 /api/sites -->
  <el-select
    :model-value="modelValue"
    placeholder="全部站点"
    clearable
    style="width: 180px"
    @update:model-value="emit('update:modelValue', $event ?? '')"
  >
    <el-option label="全部站点" value="" />
    <el-option v-for="s in sites" :key="s" :label="s" :value="s" />
  </el-select>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { getSites } from '../api/sites'

defineProps<{ modelValue: string }>()
const emit = defineEmits<{ (e: 'update:modelValue', value: string): void }>()

const sites = ref<string[]>([])

onMounted(async () => {
  try { sites.value = await getSites() } catch { sites.value = [] }
})
</script>
