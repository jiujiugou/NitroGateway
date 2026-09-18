<template>
  <div class="page-head">
    <h2 class="page-title">OPC UA 点位</h2>
    <div class="page-actions">
      <!-- ADR-073 D8：证书信任（首次连接未信任的服务器证书需先信任才能连上浏览） -->
      <el-button @click="$router.push('/opcua/certificates')">🔐 证书信任</el-button>
    </div>
  </div>
  <!-- ADR-070 层次1：OPC UA 点位按服务器地址空间（NodeId / 类型 / 访问级别）语义取点配置，
       走独立 OPC UA 点管理组件，不再套用 Modbus/S7 的通用点位表（后端接口仍协议无关）。 -->
  <OpcUaPointTable :device-id="deviceId" />
</template>

<script setup lang="ts">
import { useRoute } from 'vue-router'
import OpcUaPointTable from '../../components/OpcUaPointTable.vue'

const route = useRoute()
const deviceId = route.params.id as string
</script>

<style scoped>
.page-head { display:flex; justify-content:space-between; align-items:center; margin-bottom:16px; }
.page-title { margin-bottom:0; }
.page-actions { display:flex; gap:8px; }
</style>
