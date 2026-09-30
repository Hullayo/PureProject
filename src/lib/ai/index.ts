/**
 * AI 模块
 *
 * 使用 openai 库直接调用 DeepSeek API（OpenAI 兼容接口）。
 * 支持：非流式对话、流式输出、思考模式、函数调用。
 */

import OpenAI from 'openai';
import {
  getPrompt,
  polishUserPrompt,
  reportUserPrompt,
  breakdownUserPrompt,
} from './prompts';

function safeGet(key: string, fallback = ''): string {
  try { return localStorage.getItem(key) ?? fallback; } catch { return fallback; }
}

/** 获取 OpenAI 客户端实例（每次调用时创建，确保配置最新） */
function getClient(): OpenAI {
  const apiKey = safeGet('pm_ai_key');
  if (!apiKey) throw new Error('AI API Key 未配置，请在设置中配置');

  return new OpenAI({
    baseURL: safeGet('pm_ai_base', 'https://api.deepseek.com'),
    apiKey,
    dangerouslyAllowBrowser: true, // 浏览器环境允许
  });
}

/** 获取当前配置的模型名 */
function getModel(): string {
  return safeGet('pm_ai_model', 'deepseek-v4-flash');
}

export interface AiChatOptions {
  stream?: boolean;
  thinking?: boolean;
  reasoning_effort?: 'high' | 'max';
  temperature?: number;
  max_tokens?: number;
}

/**
 * 基础 AI 对话
 *
 * @param system - 系统提示词
 * @param user - 用户消息
 * @param options - 可选参数（流式、思考模式等）
 * @returns 模型回复文本（流式时拼接完整文本返回）
 */
export async function aiChat(
  system: string,
  user: string,
  options: AiChatOptions = {}
): Promise<string> {
  const client = getClient();
  const model = getModel();
  // 从设置读取思考模式开关
  const thinkingEnabled = options.thinking ?? safeGet('pm_ai_thinking') === 'true';
  console.log('[AI] aiChat:', { model, stream: !!options.stream, thinking: thinkingEnabled });

  const messages: OpenAI.Chat.ChatCompletionMessageParam[] = [
    { role: 'system', content: system },
    { role: 'user', content: user },
  ];

  // 构建请求参数
  const params: OpenAI.Chat.ChatCompletionCreateParamsNonStreaming = {
    model,
    messages,
    temperature: options.temperature ?? 0.7,
    max_tokens: options.max_tokens ?? 4096,
  };

  // 思考模式（仅 Pro 模型支持）
  if (options.thinking) {
    (params as any).thinking = { type: 'enabled' };
    (params as any).reasoning_effort = options.reasoning_effort ?? 'high';
  }

  // 流式输出
  if (options.stream) {
    const streamParams: OpenAI.Chat.ChatCompletionCreateParamsStreaming = {
      ...params,
      stream: true,
    };
    const stream = await client.chat.completions.create(streamParams);

    let fullContent = '';
    for await (const chunk of stream) {
      const content = chunk.choices[0]?.delta?.content || '';
      fullContent += content;
      // 可以在这里回调页面更新 UI
    }
    return fullContent;
  }

  // 非流式
  const completion = await client.chat.completions.create(params);
  return completion.choices[0]?.message?.content || '';
}

/**
 * AI 对话（流式，逐块回调）
 *
 * @param system - 系统提示词
 * @param user - 用户消息
 * @param onChunk - 每个数据块的回调
 * @param options - 可选参数
 * @returns 完整回复文本
 */
export async function aiChatStream(
  system: string,
  user: string,
  onChunk: (text: string) => void,
  options: AiChatOptions = {}
): Promise<string> {
  const client = getClient();
  const model = getModel();
  console.log('[AI] aiChatStream:', { model, thinking: !!options.thinking });

  const messages: OpenAI.Chat.ChatCompletionMessageParam[] = [
    { role: 'system', content: system },
    { role: 'user', content: user },
  ];

  const params: OpenAI.Chat.ChatCompletionCreateParamsStreaming = {
    model,
    messages,
    temperature: options.temperature ?? 0.7,
    max_tokens: options.max_tokens ?? 4096,
    stream: true,
  };

  if (options.thinking) {
    (params as any).thinking = { type: 'enabled' };
    (params as any).reasoning_effort = options.reasoning_effort ?? 'high';
  }

  const stream = await client.chat.completions.create(params);
  let fullContent = '';
  for await (const chunk of stream) {
    const content = chunk.choices[0]?.delta?.content || '';
    if (content) {
      fullContent += content;
      onChunk(content);
    }
  }
  return fullContent;
}

/** 文本润色 */
export async function polishText(text: string): Promise<string> {
  return aiChat(getPrompt('polish'), polishUserPrompt(text));
}

/** AI 拆解任务为子任务 */
export async function breakdownTask(task: {
  title: string;
  description: string;
  status: string;
  task_group_name?: string;
  status_category?: string;
  priority: string;
  due_date: string;
  existing_subtasks: string;
}): Promise<string> {
  return aiChat(getPrompt('breakdown'), breakdownUserPrompt(task));
}

/** 生成进度报告（HTML） */
export async function generateReport(data: {
  name: string;
  description: string;
  created_at: string;
  updated_at: string;
  tasks: string;
  milestones: string;
  taskCount: string;
}): Promise<string> {
  return aiChat(getPrompt('report'), reportUserPrompt(data));
}

/** 保存报告 HTML 到文档目录 */
export async function saveReport(projectName: string, html: string): Promise<string> {
  const { invoke } = await import('@tauri-apps/api/core');
  const docsDir = await invoke<string>('get_documents_dir');
  const safeName = projectName.replace(/[^a-zA-Z0-9一-鿿_-]/g, '_');
  const timestamp = new Date().toISOString().slice(0, 10);
  const path = `${docsDir}/ProjectManager/Reports/${safeName}_report_${timestamp}.html`;
  await invoke('save_text_file', { path, content: html });
  return path;
}

/** 解析 AI 拆解结果 */
export function parseBreakdownResult(text: string): Array<{
  title: string;
  estimate: string;
  dependsOn: number[];
}> {
  const lines = text.trim().split('\n').filter(l => l.trim());
  const result: Array<{ title: string; estimate: string; dependsOn: number[] }> = [];

  for (const line of lines) {
    const match = line.match(/^\d+\.\s*(.+?)\s*\|\s*预估:\s*(.+?)\s*\|\s*依赖:\s*(.+)$/);
    if (!match) continue;

    const title = match[1].trim();
    const estimate = match[2].trim();
    const depStr = match[3].trim();
    const dependsOn = depStr === '无' ? [] : depStr.split(',').map(s => parseInt(s.trim())).filter(n => !isNaN(n));

    result.push({ title, estimate, dependsOn });
  }

  return result;
}

// 导出 OpenAI 类型，方便外部使用
export type { OpenAI };
