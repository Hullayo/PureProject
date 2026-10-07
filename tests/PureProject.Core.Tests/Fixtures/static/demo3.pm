{
  "version": "1.0",
  "project": {
    "name": "移动端 App UI 设计",
    "description": "为新产品设计完整的移动端 UI 界面和交互原型",
    "template": "default",
    "color": "#ec4899",
    "created_at": "2026-05-15T09:00:00.000Z",
    "updated_at": "2026-06-01T14:00:00.000Z",
    "start_date": "2026-05-15",
    "end_date": "2026-06-10",
    "default_task_group_id": "group-7e0ef1a3"
  },
  "readme_file": "README.md",
  "template": {
    "dirs": [
      "designs",
      "prototype",
      "assets"
    ],
    "files": [
      "README.md",
      "design-system.md",
      "CHANGELOG.md"
    ],
    "file_contents": {
      "README.md": "# 移动端 App UI 设计\n\n## 设计规范\n\n- 使用 Figma 进行界面设计\n- 统一的设计语言和组件库\n- 支持 iOS 和 Android 双端适配",
      "design-system.md": "# Design System\n\n## Colors\n- Primary: #6366f1\n- Secondary: #ec4899\n- Background: #ffffff\n\n## Typography\n- Font: Inter\n- Sizes: 14/16/18/24/32px",
      "CHANGELOG.md": "# Changelog"
    }
  },
  "tasks": [
    {
      "id": "d1",
      "title": "用户调研与竞品分析",
      "description": "分析竞品 App 的 UI 设计和交互模式",
      "priority": "high",
      "tags": [
        "调研"
      ],
      "due_date": "2026-05-18",
      "start_offset": 0,
      "dependencies": [],
      "subtasks": [
        {
          "id": "sd1",
          "title": "竞品筛选",
          "done": true
        },
        {
          "id": "sd2",
          "title": "功能对比表",
          "done": true
        },
        {
          "id": "sd3",
          "title": "设计趋势报告",
          "done": true
        }
      ],
      "created_at": "2026-05-15T09:00:00.000Z",
      "updated_at": "2026-05-17T18:00:00.000Z",
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-7e0ef1a3",
      "status_id": "status-8756145d",
      "completed_at": "2026-05-17T18:00:00.000Z"
    },
    {
      "id": "d2",
      "title": "设计系统搭建",
      "description": "建立颜色、字体、间距、组件等设计规范",
      "priority": "high",
      "tags": [
        "设计"
      ],
      "due_date": "2026-05-22",
      "start_offset": 0,
      "dependencies": [],
      "created_at": "2026-05-17T10:00:00.000Z",
      "updated_at": "2026-05-21T16:00:00.000Z",
      "subtasks": [],
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-7e0ef1a3",
      "status_id": "status-8756145d",
      "completed_at": "2026-05-21T16:00:00.000Z"
    },
    {
      "id": "d3",
      "title": "首页原型设计",
      "description": "App 首页的线框图和高保真设计",
      "priority": "high",
      "tags": [
        "设计"
      ],
      "due_date": "2026-05-28",
      "start_offset": null,
      "dependencies": [
        {
          "taskId": "d2",
          "dayOffset": 0
        }
      ],
      "created_at": "2026-05-21T08:00:00.000Z",
      "updated_at": "2026-05-27T12:00:00.000Z",
      "subtasks": [],
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-7e0ef1a3",
      "status_id": "status-8756145d",
      "completed_at": "2026-05-27T12:00:00.000Z"
    },
    {
      "id": "d4",
      "title": "核心功能页面设计",
      "description": "详情页、设置页、个人中心等核心页面设计",
      "priority": "medium",
      "tags": [
        "设计"
      ],
      "due_date": "2026-06-03",
      "start_offset": null,
      "dependencies": [
        {
          "taskId": "d3",
          "dayOffset": 0
        }
      ],
      "subtasks": [
        {
          "id": "sd4",
          "title": "详情页设计",
          "done": true
        },
        {
          "id": "sd5",
          "title": "表单页面",
          "done": false
        },
        {
          "id": "sd6",
          "title": "个人中心",
          "done": false
        }
      ],
      "created_at": "2026-05-27T08:00:00.000Z",
      "updated_at": "2026-06-01T10:00:00.000Z",
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-7e0ef1a3",
      "status_id": "status-541b4fe8",
      "completed_at": null
    },
    {
      "id": "d5",
      "title": "动效与交互动画",
      "description": "页面切换动画、微交效、加载状态设计",
      "priority": "low",
      "tags": [
        "动效"
      ],
      "due_date": "2026-06-08",
      "start_offset": null,
      "dependencies": [
        {
          "taskId": "d4",
          "dayOffset": 0
        }
      ],
      "created_at": "2026-05-27T08:00:00.000Z",
      "updated_at": "2026-05-27T08:00:00.000Z",
      "subtasks": [],
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-7e0ef1a3",
      "status_id": "status-6bf65269",
      "completed_at": null
    },
    {
      "id": "d6",
      "title": "设计交付与切图",
      "description": "整理设计稿、导出切图、编写设计说明文档",
      "priority": "high",
      "tags": [
        "交付"
      ],
      "due_date": "2026-06-10",
      "start_offset": null,
      "dependencies": [
        {
          "taskId": "d5",
          "dayOffset": 0
        }
      ],
      "created_at": "2026-05-27T08:00:00.000Z",
      "updated_at": "2026-05-27T08:00:00.000Z",
      "subtasks": [],
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-7e0ef1a3",
      "status_id": "status-6bf65269",
      "completed_at": null
    }
  ],
  "tags": [
    {
      "id": "td1",
      "name": "调研",
      "color": "#8b5cf6"
    },
    {
      "id": "td2",
      "name": "设计",
      "color": "#ec4899"
    },
    {
      "id": "td3",
      "name": "动效",
      "color": "#06b6d4"
    },
    {
      "id": "td4",
      "name": "交付",
      "color": "#10b981"
    }
  ],
  "changelog": [
    {
      "version": "1.0.0",
      "date": "2026-05-15",
      "info": "项目启动，完成用户调研"
    },
    {
      "version": "1.1.0",
      "date": "2026-05-22",
      "info": "设计系统搭建完成"
    },
    {
      "version": "1.2.0",
      "date": "2026-05-28",
      "info": "首页原型设计完成"
    }
  ],
  "milestones": [
    {
      "id": "md1",
      "title": "设计评审",
      "date": "2026-06-03",
      "color": "#ec4899",
      "description": "核心页面设计评审"
    },
    {
      "id": "md2",
      "title": "设计交付",
      "date": "2026-06-10",
      "color": "#10b981",
      "description": "全部设计稿交付开发"
    }
  ],
  "schema_version": 4,
  "task_groups": [
    {
      "id": "group-7e0ef1a3",
      "name": "默认任务组",
      "sort_order": 1024,
      "archived": false,
      "initial_status_id": "status-6bf65269",
      "completion_status_id": "status-8756145d",
      "statuses": [
        {
          "id": "status-8756145d",
          "name": "已完成",
          "color": "#10b981",
          "category": "done",
          "sort_order": 1024
        },
        {
          "id": "status-541b4fe8",
          "name": "进行中",
          "color": "#4f46e5",
          "category": "active",
          "sort_order": 2048
        },
        {
          "id": "status-6bf65269",
          "name": "待办",
          "color": "#6b7280",
          "category": "todo",
          "sort_order": 3072
        }
      ]
    }
  ]
}
