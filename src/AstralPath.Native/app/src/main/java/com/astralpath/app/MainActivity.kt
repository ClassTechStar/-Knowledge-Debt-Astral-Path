package com.astralpath.app

import android.annotation.SuppressLint
import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.webkit.ValueCallback
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.webkit.WebViewAssetLoader

/**
 * 与 Windows（WebView2）/ Web 完全同构：
 * 加载同一份 www/index.html，UI 与前端逻辑逐字节一致。
 * Android 仅作壳 + 性能（硬件加速、DOM 存储、无缩放抖动）。
 */
class MainActivity : AppCompatActivity() {
    private lateinit var web: WebView
    private lateinit var assetLoader: WebViewAssetLoader

    // ── 文件选择支持：<input type="file"> 依赖 WebChromeClient.onShowFileChooser ──
    private var filePathCallback: ValueCallback<Array<Uri>>? = null
    private val fileChooserLauncher =
        registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            val cb = filePathCallback
            filePathCallback = null
            if (cb == null) return@registerForActivityResult
            val uris = if (result.resultCode == Activity.RESULT_OK) parsePickerResult(result.data) else null
            android.util.Log.i(
                "AstralPathPicker",
                "picker result: code=${result.resultCode} dataUri=${result.data?.data} " +
                    "clip=${result.data?.clipData} extras=${result.data?.extras?.keySet()} uris=${uris?.contentToString()}"
            )
            // 验证返回的 content:// URI 在本进程是否真的可读（诊断 MIUI“安全访问”授权链路）
            uris?.forEach { u ->
                runCatching {
                    val name = contentResolver.query(u, null, null, null, null)?.use { c ->
                        if (c.moveToFirst()) c.getString(c.getColumnIndex(android.provider.OpenableColumns.DISPLAY_NAME)) else null
                    }
                    val readable = contentResolver.openInputStream(u)?.use { s -> s.read() >= 0 } == true
                    android.util.Log.i("AstralPathPicker", "uri=$u name=$name readable=$readable")
                }.onFailure { android.util.Log.w("AstralPathPicker", "uri=$u check failed: $it") }
            }
            cb.onReceiveValue(uris ?: arrayOf())
        }

    /**
     * 解析文件选择器返回的 URI。
     * AOSP DocumentsUI 把 URI 放在 intent.data / ClipData，FileChooserParams.parseResult 可直接解析；
     * 但 MIUI/HyperOS 的“安全访问”选择链路（photopicker → fileexplorer）把 URI 放进
     * Intent.EXTRA_STREAM（ArrayList 或单个 Uri）返回，parseResult 拿到 null，
     * 于是在 WebView 的 input type=file 上永远收不到文件（change 事件不触发）。
     */
    private fun parsePickerResult(data: Intent?): Array<Uri>? {
        if (data == null) return null
        WebChromeClient.FileChooserParams.parseResult(Activity.RESULT_OK, data)?.let {
            if (it.isNotEmpty()) return it
        }
        val out = ArrayList<Uri>()
        when (val stream = data.extras?.get(Intent.EXTRA_STREAM)) {
            is Uri -> out.add(stream)
            is List<*> -> for (item in stream) when (item) {
                is Uri -> out.add(item)
                is String -> runCatching { Uri.parse(item) }.getOrNull()?.let { u -> out.add(u) }
            }
        }
        return if (out.isNotEmpty()) out.toTypedArray() else null
    }

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        web = WebView(this).apply {
            // 性能：强制硬件层，列表/滚动更顺
            setLayerType(android.view.View.LAYER_TYPE_HARDWARE, null)
        }
        setContentView(web)
        assetLoader = WebViewAssetLoader.Builder()
            .addPathHandler("/assets/", WebViewAssetLoader.AssetsPathHandler(this))
            .build()
        web.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            databaseEnabled = true
            allowFileAccess = true
            // 文件选择器返回的是 content:// URI，必须允许 WebView 读取
            allowContentAccess = true
            useWideViewPort = true
            loadWithOverviewMode = true
            setSupportZoom(false)
            builtInZoomControls = false
            displayZoomControls = false
            mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
            cacheMode = WebSettings.LOAD_DEFAULT
            textZoom = 100
            mediaPlaybackRequiresUserGesture = false
            // 大页 DOM 更稳
            setGeolocationEnabled(false)
        }
        web.webChromeClient = object : WebChromeClient() {
            override fun onJsAlert(view: WebView?, url: String?, message: String?, result: android.webkit.JsResult?): Boolean {
                Toast.makeText(this@MainActivity, message ?: "", Toast.LENGTH_SHORT).show()
                result?.confirm()
                return true
            }

            override fun onConsoleMessage(consoleMessage: android.webkit.ConsoleMessage?): Boolean {
                android.util.Log.w(
                    "AstralPathJS",
                    "[${consoleMessage?.messageLevel()}] ${consoleMessage?.message()} @ ${consoleMessage?.sourceId()}:${consoleMessage?.lineNumber()}"
                )
                return true
            }

            /**
             * 文件选择（藏书阁「上传并解析」）：
             * 不实现此回调时 <input type="file"> 在 Android WebView 上点击完全无响应。
             */
            override fun onShowFileChooser(
                webView: WebView?,
                callback: ValueCallback<Array<Uri>>?,
                params: FileChooserParams?
            ): Boolean {
                // 防重复回调：上一次未消费的回调先置空，避免 WebView 后续拒绝再次触发
                filePathCallback?.onReceiveValue(null)
                filePathCallback = callback ?: return false
                val intent = Intent(Intent.ACTION_GET_CONTENT).apply {
                    addCategory(Intent.CATEGORY_OPENABLE)
                    type = "*/*"
                    // 前端 accept=.pdf,.txt,.md…（扩展名形式），映射为 MIME 以便 DocumentsUI 过滤
                    putExtra(Intent.EXTRA_MIME_TYPES, arrayOf(
                        "application/pdf", "text/plain", "text/markdown",
                        "application/x-pdf", "text/x-markdown"
                    ))
                    if (params?.mode == FileChooserParams.MODE_OPEN_MULTIPLE) {
                        putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true)
                    }
                }
                return try {
                    fileChooserLauncher.launch(intent)
                    true
                } catch (_: ActivityNotFoundException) {
                    // 无系统文件选择器时回退为不过滤类型再试一次
                    try {
                        fileChooserLauncher.launch(Intent(Intent.ACTION_GET_CONTENT).apply {
                            addCategory(Intent.CATEGORY_OPENABLE); type = "*/*"
                            if (params?.mode == FileChooserParams.MODE_OPEN_MULTIPLE) putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true)
                        })
                        true
                    } catch (_: Exception) {
                        filePathCallback = null
                        Toast.makeText(this@MainActivity, "系统未提供文件选择器", Toast.LENGTH_SHORT).show()
                        false
                    }
                }
            }
        }
        web.webViewClient = object : WebViewClient() {
            // 导航白名单：只允许本地资产域，外链交给系统浏览器
            override fun shouldOverrideUrlLoading(view: WebView?, request: WebResourceRequest?): Boolean {
                val uri = request?.url ?: return false
                val host = uri.host ?: ""
                return if (host == "appassets.androidplatform.net") false
                else {
                    try {
                        startActivity(android.content.Intent(android.content.Intent.ACTION_VIEW, uri))
                    } catch (_: Exception) { /* 无浏览器时忽略 */ }
                    true
                }
            }
            override fun shouldInterceptRequest(view: WebView?, request: WebResourceRequest?): WebResourceResponse? {
                val uri = request?.url ?: return null
                return if (uri.host == "appassets.androidplatform.net") assetLoader.shouldInterceptRequest(uri)
                else super.shouldInterceptRequest(view, request)
            }
        }

        // 同一份前端：与 Web/Desktop 的 index.html 逐字节一致（sync-monolith-html.ps1 md5 门禁）。
        // P2 审计 M6：壳不再注入任何脚本改写前端语义（原 injectOffline 注入的
        // __ASTRALPATH_OFFLINE__ 旗标当前页面早已不读取）——壳只做纯加载器。
        web.loadUrl("https://appassets.androidplatform.net/assets/www/index.html")
    }

    override fun onPause() {
        web.onPause()
        super.onPause()
    }

    override fun onResume() {
        web.onResume()
        super.onResume()
    }

    override fun onDestroy() {
        web.destroy()
        super.onDestroy()
    }
}
