package com.astralpath.app

import android.annotation.SuppressLint
import android.app.Activity
import android.os.Bundle
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.Toast
import androidx.webkit.WebViewAssetLoader
import java.io.BufferedReader

/** 与 Web / Windows 同一 UI：内嵌 www/index.html + 本机 REST */
class MainActivity : Activity() {
    private lateinit var web: WebView
    private lateinit var assetLoader: WebViewAssetLoader
    private val apiBase = "http://127.0.0.1:5190/"

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        web = WebView(this)
        setContentView(web)
        assetLoader = WebViewAssetLoader.Builder()
            .addPathHandler("/assets/", WebViewAssetLoader.AssetsPathHandler(this))
            .build()
        web.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            allowFileAccess = true
            useWideViewPort = true
            loadWithOverviewMode = true
            mixedContentMode = WebSettings.MIXED_CONTENT_ALWAYS_ALLOW
            textZoom = 100
        }
        web.webChromeClient = object : WebChromeClient() {
            override fun onJsAlert(view: WebView?, url: String?, message: String?, result: android.webkit.JsResult?): Boolean {
                Toast.makeText(this@MainActivity, message ?: "", Toast.LENGTH_SHORT).show()
                result?.confirm()
                return true
            }
        }
        web.webViewClient = object : WebViewClient() {
            override fun shouldInterceptRequest(view: WebView?, request: WebResourceRequest?): WebResourceResponse? {
                val uri = request?.url ?: return null
                return if (uri.host == "appassets.androidplatform.net") assetLoader.shouldInterceptRequest(uri)
                else super.shouldInterceptRequest(view, request)
            }
        }
        val htmlRaw = assets.open("www/index.html").bufferedReader().use(BufferedReader::readText)
        // 手机独立：探测不到电脑 API 时自动进入离线核心（不依赖 adb reverse / 局域网）
        val html = if (htmlRaw.contains("__ASTRALPATH_OFFLINE__=true") || htmlRaw.contains("__ASTRALPATH_OFFLINE__ = true")) {
            htmlRaw
        } else {
            htmlRaw.replaceFirst("<script>", "<script>window.__ASTRALPATH_OFFLINE__=false;window.__ASTRALPATH_FORCE_ONLINE__=false;</script><script>", ignoreCase = true)
        }
        web.loadDataWithBaseURL("https://appassets.androidplatform.net/assets/www/", html, "text/html", "utf-8", null)
        // 后台探测本机/电脑 API；失败则重载为强制离线
        Thread {
            val online = try {
                java.net.URL("http://127.0.0.1:5190/health/ready").openConnection().apply { connectTimeout = 1500; readTimeout = 1500 }.getInputStream().use { it.read() >= 0 }
            } catch (_: Exception) { false }
            if (!online) {
                runOnUiThread {
                    val offlineHtml = html.replaceFirst(
                        "window.__ASTRALPATH_OFFLINE__=false",
                        "window.__ASTRALPATH_OFFLINE__=true",
                        ignoreCase = true
                    ).let {
                        if (it.contains("__ASTRALPATH_OFFLINE__=true")) it
                        else it.replaceFirst("<script>", "<script>window.__ASTRALPATH_OFFLINE__=true;</script><script>", ignoreCase = true)
                    }
                    web.loadDataWithBaseURL("https://appassets.androidplatform.net/assets/www/", offlineHtml, "text/html", "utf-8", null)
                }
            }
        }.start()
    }
}
