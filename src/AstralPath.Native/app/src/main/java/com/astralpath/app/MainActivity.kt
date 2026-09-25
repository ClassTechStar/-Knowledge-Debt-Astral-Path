package com.astralpath.app

import android.annotation.SuppressLint
import android.os.Bundle
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.webkit.WebViewAssetLoader
import java.io.BufferedReader

/**
 * 与 Windows（WebView2）/ Web 完全同构：
 * 加载同一份 www/index.html，UI 与前端逻辑逐字节一致。
 * Android 仅作壳 + 性能（硬件加速、DOM 存储、无缩放抖动）。
 */
class MainActivity : AppCompatActivity() {
    private lateinit var web: WebView
    private lateinit var assetLoader: WebViewAssetLoader

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
            allowContentAccess = false
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

        // 同一份前端：与 Web/Desktop 的 wwwroot/index.html 相同
        val htmlRaw = assets.open("www/index.html").bufferedReader().use(BufferedReader::readText)
        // 手机优先走离线核心；若连上电脑 API 则由前端自动切回
        val html = injectOffline(htmlRaw)
        web.loadDataWithBaseURL(
            "https://appassets.androidplatform.net/assets/www/",
            html, "text/html", "utf-8", null
        )
    }

    private fun injectOffline(raw: String): String {
        if (raw.contains("__ASTRALPATH_OFFLINE__=true")) return raw
        val flag = "<script>window.__ASTRALPATH_OFFLINE__=true;window.__ASTRALPATH_FORCE_ONLINE__=false;</script>"
        val i = raw.indexOf("<script>", ignoreCase = true)
        return if (i >= 0) raw.substring(0, i) + flag + raw.substring(i) else flag + raw
    }

    override fun onPause() {
        web.onPause()
        super.onPause()
    }

    override fun onResume() {
        super.onResume()
        web.onResume()
    }

    override fun onDestroy() {
        web.destroy()
        super.onDestroy()
    }
}
