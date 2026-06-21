<template>
  <span :class="['tag', status.toLowerCase()]">{{ status }}</span>
</template>
<script setup lang="ts">defineProps<{status:string}>()</script>
<style scoped>
.tag { display:inline-block; padding:2px 10px; border-radius:10px; font-size:12px; font-weight:600; }
.online { background:rgba(103,194,58,.12); color:#67c23a; } .offline { background:rgba(245,108,108,.12); color:#f56c6c; }
.error { background:rgba(230,162,60,.12); color:#e6a23c; } .unknown { background:rgba(160,174,192,.12); color:#a0aec0; } .maintenance { background:rgba(144,96,235,.12); color:#9060eb; }
</style>
