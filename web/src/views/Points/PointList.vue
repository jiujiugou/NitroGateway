<template>
  <div class="page-head">
    <h2 class="page-title">点位管理</h2>
    <span class="page-sub">Modbus / S7</span>
  </div>
  <!-- ADR-070 层次1：通用点位表（Modbus/S7：批量生成；OPC UA 走 /opcua/:id/points 独立分区） -->
  <PointTable :device-id="deviceId" batch-enabled />
</template>

<script setup lang="ts">
import { useRoute } from 'vue-router'
import PointTable from '../../components/PointTable.vue'

const route = useRoute()
const deviceId = route.params.deviceId as string
</script>

<style scoped>
.page-head { display:flex; justify-content:space-between; align-items:center; margin-bottom:16px; }
.page-title { margin-bottom:0; }
.page-sub { color:var(--text-muted); font-size:12px; }
</style>
