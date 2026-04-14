export default {
  async fetch(request, env, ctx) {
    // 🛡️ 防扫描：非 POST/OPTIONS 一律返回 404 空页面
    if (request.method === 'OPTIONS') {
      return new Response(null, {
        headers: {
          'Access-Control-Allow-Origin': '*',
          'Access-Control-Allow-Methods': 'POST, OPTIONS',
          'Access-Control-Allow-Headers': 'Content-Type, User-Agent',
        }
      });
    }
    if (request.method !== 'POST') {
      return new Response('', { status: 404 });
    }

    // 🛡️ 门禁暗号安全墙
    if (!request.headers.get("User-Agent")?.includes("MuseDashTOOL-ChartUpload")) {
      return new Response('', { status: 404 });
    }

    const BUCKET = env.BUCKET;
    if (!BUCKET) {
      return jsonResponse(false, "服务端未绑定 R2 存储桶");
    }

    // 所有上传的谱面存放在"未经审查"文件夹下
    const PREFIX = "未经审查";

    try {
      const formData = await request.formData();

      const chart_id     = formData.get('chart_id') || "";
      const original_id  = formData.get('original_id') || "";
      const artist       = formData.get('artist') || "";
      const charter      = formData.get('charter') || "";
      const bpm          = formData.get('bpm') || "";
      const b_url        = formData.get('b_url') || "";

      const difficulties_json = formData.get('difficulties_json');
      const difficulties = JSON.parse(difficulties_json || '[]');

      const mdmFile   = formData.get('mdm');
      const coverFile = formData.get('cover');
      const demoFile  = formData.get('demo');

      if (!mdmFile || !coverFile || !demoFile) {
        throw new Error("缺失必要的文件 (mdm/cover/demo)，终止动作。");
      }

      // ⏰ 生成北京时间时间戳
      const date = new Date();
      date.setHours(date.getUTCHours() + 8);
      const uploadTime = `${date.getUTCFullYear()}/${String(date.getUTCMonth() + 1).padStart(2, '0')}/${String(date.getUTCDate()).padStart(2, '0')} ${String(date.getUTCHours()).padStart(2, '0')}:${String(date.getUTCMinutes()).padStart(2, '0')}:${String(date.getUTCSeconds()).padStart(2, '0')}`;

      // ========================================================
      // 🔍 读取 index.json，检测是否有同名谱面
      // ========================================================
      const INDEX_KEY = `${PREFIX}/index.json`;
      let indexData = [];

      const indexObj = await BUCKET.get(INDEX_KEY);
      if (indexObj) {
        try {
          const text = await indexObj.text();
          indexData = JSON.parse(text);
          if (!Array.isArray(indexData)) indexData = [];
        } catch (e) {
          throw new Error("云端 index.json 文件语法受损，为了防删库终止数据写入");
        }
      }

      // 用 original_id（谱面名字）查找是否已存在
      const existingEntry = indexData.find(item => item.original_id === original_id);
      const isDuplicate = !!existingEntry;

      // 如果是重复谱面，用旧的 id，文件路径完全一致直接覆盖
      const finalId = isDuplicate ? existingEntry.id : chart_id;

      const finalCoverName = isDuplicate ? existingEntry.cover_url : decodeURIComponent(coverFile.name);
      const finalDemoName  = isDuplicate ? existingEntry.demo_url  : decodeURIComponent(demoFile.name);
      const finalMdmName   = isDuplicate ? existingEntry.download_url : decodeURIComponent(mdmFile.name);

      // ========================================================
      // 📤 上传文件到 R2
      // ========================================================
      // 上传封面
      await BUCKET.put(
        `${PREFIX}/covers/${finalCoverName}`,
        await coverFile.arrayBuffer(),
        { httpMetadata: { contentType: coverFile.type || 'image/png' } }
      );

      // 上传试听
      await BUCKET.put(
        `${PREFIX}/demos/${finalDemoName}`,
        await demoFile.arrayBuffer(),
        { httpMetadata: { contentType: demoFile.type || 'audio/ogg' } }
      );

      // 上传 MDM 谱面文件
      await BUCKET.put(
        `${PREFIX}/mdm/${finalMdmName}`,
        await mdmFile.arrayBuffer(),
        { httpMetadata: { contentType: 'application/octet-stream' } }
      );

      // ========================================================
      // 📝 更新 index.json
      // ========================================================
      const newItem = {
        id: finalId,
        original_id,
        artist,
        charter,
        bpm,
        difficulties,
        cover_url: finalCoverName,
        demo_url: finalDemoName,
        download_url: finalMdmName,
        B_url: b_url,
        upload_time: uploadTime,
      };

      // 移除旧条目（用 original_id 和 id 双重匹配清理干净）
      indexData = indexData.filter(item => item.id !== finalId && item.original_id !== original_id);
      indexData.push(newItem);

      // 写回 index.json
      await BUCKET.put(INDEX_KEY, JSON.stringify(indexData, null, 2), {
        httpMetadata: { contentType: 'application/json; charset=utf-8' }
      });

      const msg = isDuplicate
        ? '检测到同名谱面，已覆盖更新所有文件和信息。'
        : '上传成功，所有资源均已入库！';

      return jsonResponse(true, msg);

    } catch (e) {
      return jsonResponse(false, e.message);
    }
  }
};

function jsonResponse(success, message, status = 200) {
  return new Response(JSON.stringify({ success, message }), {
    status,
    headers: {
      'Content-Type': 'application/json',
      'Access-Control-Allow-Origin': '*',
    }
  });
}
