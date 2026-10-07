{
  "version": "1.0",
  "project": {
    "name": "智能小车项目",
    "description": "基于STM32的智能避障小车，包含硬件设计和嵌入式软件",
    "template": "hardware",
    "color": "#4f46e5",
    "created_at": "2026-05-20T08:00:00.000Z",
    "updated_at": "2026-05-27T10:00:00.000Z",
    "start_date": "2026-05-20",
    "end_date": "2026-06-15",
    "default_task_group_id": "group-b9fee6f8"
  },
  "readme_file": "README.md",
  "template": {
    "dirs": [
      "Hardware",
      "Software",
      "Firmware",
      "Documentation",
      "PCB",
      "BOM",
      "Datasheets"
    ],
    "files": [
      "README.md",
      ".gitignore",
      "CHANGELOG.md"
    ],
    "file_contents": {
      "README.md": "# 智能小车项目\n\n基于STM32的智能避障小车，包含硬件设计和嵌入式软件。\n\n## 功能特性\n\n- 超声波避障\n- 红外循迹\n- 蓝牙遥控\n- PWM调速\n\n## 硬件清单\n\n| 模块 | 型号 | 数量 |\n|------|------|------|\n| 主控 | STM32F103C8T6 | 1 |\n| 电机驱动 | L298N | 1 |\n| 超声波 | HC-SR04 | 2 |\n| 电池 | 18650 | 2 |\n\n## 目录结构\n\n```\n├── Hardware/     # 硬件设计文件\n├── Software/     # 上位机软件\n├── Firmware/     # 固件源码\n├── Documentation/# 文档\n├── PCB/          # PCB设计\n├── BOM/          # 物料清单\n└── Datasheets/   # 数据手册\n```",
      ".gitignore": "*.sch\n*.brd\n*.backup\n*.depend\n*.o\n*.elf\n*.hex\n*.bak\n*.log\n",
      "CHANGELOG.md": "# Changelog\n\n## [1.0.0] - 2026-05-20\n### Added\n- Initial release"
    }
  },
  "tasks": [
    {
      "id": "t1",
      "title": "完成PCB原理图设计",
      "description": "使用KiCad绘制主控板原理图，包含STM32最小系统、电机驱动、传感器接口",
      "priority": "high",
      "tags": [
        "硬件",
        "PCB"
      ],
      "due_date": "2026-05-24",
      "start_offset": 0,
      "dependencies": [],
      "subtasks": [
        {
          "id": "s1",
          "title": "绘制STM32最小系统",
          "done": true
        },
        {
          "id": "s2",
          "title": "电机驱动接口",
          "done": true
        },
        {
          "id": "s3",
          "title": "传感器接口",
          "done": true
        }
      ],
      "created_at": "2026-05-20T08:00:00.000Z",
      "updated_at": "2026-05-22T18:00:00.000Z",
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-b9fee6f8",
      "status_id": "status-d6b93a9c",
      "completed_at": "2026-05-22T18:00:00.000Z"
    },
    {
      "id": "t2",
      "title": "PCB布局与打样",
      "description": "完成PCB布局布线，生成Gerber文件，发送到嘉立创打样",
      "priority": "high",
      "tags": [
        "硬件",
        "PCB"
      ],
      "due_date": null,
      "start_offset": null,
      "dependencies": [
        {
          "taskId": "t1",
          "dayOffset": 0
        }
      ],
      "created_at": "2026-05-22T18:00:00.000Z",
      "updated_at": "2026-05-25T10:00:00.000Z",
      "subtasks": [],
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-b9fee6f8",
      "status_id": "status-aebba56f",
      "completed_at": null
    },
    {
      "id": "t3",
      "title": "采购元器件",
      "description": "根据BOM清单采购所有元器件",
      "priority": "medium",
      "tags": [
        "采购"
      ],
      "due_date": "2026-05-26",
      "start_offset": 0,
      "dependencies": [],
      "created_at": "2026-05-20T08:00:00.000Z",
      "updated_at": "2026-05-24T16:00:00.000Z",
      "subtasks": [],
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-b9fee6f8",
      "status_id": "status-d6b93a9c",
      "completed_at": "2026-05-24T16:00:00.000Z"
    },
    {
      "id": "t4",
      "title": "焊接与组装",
      "description": "PCB到货后焊接元器件，组装小车底盘",
      "priority": "high",
      "tags": [
        "硬件"
      ],
      "due_date": null,
      "start_offset": null,
      "dependencies": [
        {
          "taskId": "t2",
          "dayOffset": 0
        },
        {
          "taskId": "t3",
          "dayOffset": 0
        }
      ],
      "created_at": "2026-05-25T08:00:00.000Z",
      "updated_at": "2026-05-25T08:00:00.000Z",
      "subtasks": [],
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-b9fee6f8",
      "status_id": "status-ab205164",
      "completed_at": null
    },
    {
      "id": "t5",
      "title": "编写电机驱动程序",
      "description": "实现PWM控制直流电机，支持正反转和调速",
      "priority": "medium",
      "tags": [
        "固件",
        "驱动"
      ],
      "due_date": null,
      "start_offset": 2,
      "dependencies": [],
      "created_at": "2026-05-20T08:00:00.000Z",
      "updated_at": "2026-05-20T08:00:00.000Z",
      "subtasks": [],
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-b9fee6f8",
      "status_id": "status-ab205164",
      "completed_at": null
    },
    {
      "id": "t6",
      "title": "超声波避障算法",
      "description": "实现HC-SR04测距，编写避障逻辑",
      "priority": "medium",
      "tags": [
        "固件",
        "算法"
      ],
      "due_date": null,
      "start_offset": null,
      "dependencies": [
        {
          "taskId": "t5",
          "dayOffset": 2
        }
      ],
      "created_at": "2026-05-20T08:00:00.000Z",
      "updated_at": "2026-05-20T08:00:00.000Z",
      "subtasks": [],
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-b9fee6f8",
      "status_id": "status-ab205164",
      "completed_at": null
    },
    {
      "id": "t7",
      "title": "系统联调测试",
      "description": "硬件+固件联合调试，测试避障功能",
      "priority": "high",
      "tags": [
        "测试"
      ],
      "due_date": null,
      "start_offset": null,
      "dependencies": [
        {
          "taskId": "t4",
          "dayOffset": 0
        },
        {
          "taskId": "t6",
          "dayOffset": 0
        }
      ],
      "subtasks": [
        {
          "id": "s4",
          "title": "电机驱动测试",
          "done": false
        },
        {
          "id": "s5",
          "title": "超声波测距校准",
          "done": false
        },
        {
          "id": "s6",
          "title": "避障逻辑验证",
          "done": false
        },
        {
          "id": "s7",
          "title": "蓝牙遥控测试",
          "done": false
        }
      ],
      "created_at": "2026-05-20T08:00:00.000Z",
      "updated_at": "2026-05-20T08:00:00.000Z",
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-b9fee6f8",
      "status_id": "status-ab205164",
      "completed_at": null
    },
    {
      "id": "t8",
      "title": "编写项目文档",
      "description": "整理设计文档、接线说明、使用说明",
      "priority": "low",
      "tags": [
        "文档"
      ],
      "due_date": "2026-06-15",
      "start_offset": null,
      "dependencies": [
        {
          "taskId": "t7",
          "dayOffset": 0
        }
      ],
      "created_at": "2026-05-20T08:00:00.000Z",
      "updated_at": "2026-05-20T08:00:00.000Z",
      "subtasks": [],
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-b9fee6f8",
      "status_id": "status-ab205164",
      "completed_at": null
    }
  ],
  "tags": [
    {
      "id": "tag1",
      "name": "硬件",
      "color": "#ef4444"
    },
    {
      "id": "tag2",
      "name": "PCB",
      "color": "#f59e0b"
    },
    {
      "id": "tag3",
      "name": "固件",
      "color": "#3b82f6"
    },
    {
      "id": "tag4",
      "name": "驱动",
      "color": "#8b5cf6"
    },
    {
      "id": "tag5",
      "name": "算法",
      "color": "#06b6d4"
    },
    {
      "id": "tag6",
      "name": "采购",
      "color": "#10b981"
    },
    {
      "id": "tag7",
      "name": "测试",
      "color": "#ec4899"
    },
    {
      "id": "tag8",
      "name": "文档",
      "color": "#6b7280"
    }
  ],
  "changelog": [
    {
      "version": "1.0.0",
      "date": "2026-05-20",
      "info": "项目启动，完成需求分析"
    },
    {
      "version": "1.1.0",
      "date": "2026-05-22",
      "info": "完成PCB原理图设计"
    },
    {
      "version": "1.2.0",
      "date": "2026-05-25",
      "info": "元器件采购完成，PCB送样"
    }
  ],
  "milestones": [
    {
      "id": "ms1",
      "title": "硬件原型完成",
      "date": "2026-05-28",
      "color": "#f59e0b",
      "description": "PCB打样到货，完成焊接和组装"
    },
    {
      "id": "ms2",
      "title": "固件开发完成",
      "date": "2026-06-05",
      "color": "#3b82f6",
      "description": "电机驱动和避障算法全部完成"
    },
    {
      "id": "ms3",
      "title": "项目交付",
      "date": "2026-06-15",
      "color": "#10b981",
      "description": "联调测试通过，文档整理完成"
    }
  ],
  "schema_version": 4,
  "task_groups": [
    {
      "id": "group-b9fee6f8",
      "name": "默认任务组",
      "sort_order": 1024,
      "archived": false,
      "initial_status_id": "status-ab205164",
      "completion_status_id": "status-d6b93a9c",
      "statuses": [
        {
          "id": "status-d6b93a9c",
          "name": "已完成",
          "color": "#10b981",
          "category": "done",
          "sort_order": 1024
        },
        {
          "id": "status-aebba56f",
          "name": "进行中",
          "color": "#4f46e5",
          "category": "active",
          "sort_order": 2048
        },
        {
          "id": "status-ab205164",
          "name": "待办",
          "color": "#6b7280",
          "category": "todo",
          "sort_order": 3072
        }
      ]
    }
  ]
}
