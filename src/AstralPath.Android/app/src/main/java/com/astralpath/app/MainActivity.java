package com.astralpath.app;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.os.Bundle;
import android.util.Log;
import android.view.KeyEvent;
import android.view.View;
import android.view.WindowManager;
import android.webkit.ConsoleMessage;
import android.webkit.WebChromeClient;
import android.widget.Toast;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;

import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;

/**
 * 知债：星穹学途 · Android 原生壳 + 与 Web / Windows 同源的业务 UI
 *
 * <p>加载顺序：优先 {@code www/index.html}（**与 Web 端 wwwroot/index.html 逐字节同一份**，
 * 保证界面、交互与数据处理完全一致）；仅当该文件缺失时回退到 {@code www/mobile.html}。
 * 业务接口与 Web / Windows 完全一致（同一 AstralPath.Api，经 adb reverse 或 10.0.2.2 访问）。</p>
 */
public class MainActivity extends Activity {

    private static final String TAG = "AstralPath";
    public static final String API_BASE = "http://127.0.0.1:5190/";

    /** 文件选择请求码：对应 Web 端「藏书阁 → 上传教材」的 <input type="file">。 */
    private static final int REQ_FILE_CHOOSER = 1001;

    private WebView web;
    /** WebView 发起的文件选择回调（onShowFileChooser 时挂起，选完文件后回调）。 */
    private android.webkit.ValueCallback<android.net.Uri[]> pendingFileCallback;

    @SuppressLint("SetJavaScriptEnabled")
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        // 浅色系统栏 + 尽量铺满（黑边在 Java 侧处理，避免主题资源缺失）
        getWindow().setStatusBarColor(0xFFF3F4F5);
        getWindow().setNavigationBarColor(0xFFF3F4F5);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_DRAWS_SYSTEM_BAR_BACKGROUNDS);
        try {
            getWindow().getDecorView().setSystemUiVisibility(
                    View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                            | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                            | View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR
                            | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR);
        } catch (Throwable ignored) { }

        setContentView(R.layout.activity_main);
        web = findViewById(R.id.web);
        // WebView 铺满，避免嵌套滚动容器
        if (web.getParent() instanceof android.view.ViewGroup) {
            android.view.ViewGroup p = (android.view.ViewGroup) web.getParent();
            android.view.ViewGroup.LayoutParams lp = web.getLayoutParams();
            lp.width = android.view.ViewGroup.LayoutParams.MATCH_PARENT;
            lp.height = android.view.ViewGroup.LayoutParams.MATCH_PARENT;
            web.setLayoutParams(lp);
        }

        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setAllowFileAccess(true);
        s.setAllowContentAccess(true);
        s.setUseWideViewPort(true);
        s.setLoadWithOverviewMode(true);
        s.setSupportZoom(true);
        s.setBuiltInZoomControls(false);
        s.setTextZoom(100);
        s.setCacheMode(WebSettings.LOAD_DEFAULT);
        s.setMediaPlaybackRequiresUserGesture(false);
        s.setBlockNetworkImage(false);
        s.setMixedContentMode(WebSettings.MIXED_CONTENT_ALWAYS_ALLOW);
        // 流畅：整页用默认合成，避免超大硬件层；隐藏滚动条
        web.setLayerType(View.LAYER_TYPE_NONE, null);
        web.setOverScrollMode(View.OVER_SCROLL_NEVER);
        web.setHorizontalScrollBarEnabled(false);
        web.setVerticalScrollBarEnabled(false);
        web.setBackgroundColor(0xFFF3F4F5);

        web.setWebChromeClient(new WebChromeClient() {
            @Override
            public boolean onConsoleMessage(ConsoleMessage cm) {
                Log.d(TAG, String.valueOf(cm == null ? "" : cm.message()));
                return true;
            }

            @Override
            public boolean onJsAlert(WebView view, String url, String message, android.webkit.JsResult result) {
                Toast.makeText(MainActivity.this, message == null ? "" : message, Toast.LENGTH_SHORT).show();
                if (result != null) result.confirm();
                return true;
            }

            @Override
            public boolean onJsConfirm(WebView view, String url, String message, android.webkit.JsResult result) {
                Toast.makeText(MainActivity.this, message == null ? "" : message, Toast.LENGTH_SHORT).show();
                if (result != null) result.confirm();
                return true;
            }

            /**
             * 文件选择：Web 端「藏书阁」用 &lt;input type="file"&gt; 上传教材（PDF/TXT），
             * 系统 WebView 默认不处理，会导致该功能在 Android 上静默失效。
             * 此处接入系统文件选择器（支持多选），保证上传链路与 Web / Windows 一致。
             */
            @Override
            public boolean onShowFileChooser(WebView view,
                                             android.webkit.ValueCallback<android.net.Uri[]> filePathCallback,
                                             FileChooserParams params) {
                if (pendingFileCallback != null) {
                    pendingFileCallback.onReceiveValue(null);   // 丢弃未完成的旧请求，避免回调泄漏
                }
                pendingFileCallback = filePathCallback;

                android.content.Intent intent = new android.content.Intent(android.content.Intent.ACTION_GET_CONTENT);
                intent.addCategory(android.content.Intent.CATEGORY_OPENABLE);
                intent.setType("*/*");
                // 与 Web 端 accept 对齐：教材以 PDF / 文本为主
                intent.putExtra(android.content.Intent.EXTRA_MIME_TYPES,
                        new String[] { "application/pdf", "text/plain", "application/octet-stream" });
                if (params != null && params.getMode() == FileChooserParams.MODE_OPEN_MULTIPLE) {
                    intent.putExtra(android.content.Intent.EXTRA_ALLOW_MULTIPLE, true);
                }
                try {
                    startActivityForResult(android.content.Intent.createChooser(intent, "选择教材文件"), REQ_FILE_CHOOSER);
                    return true;
                } catch (Exception e) {
                    Log.e(TAG, "file chooser unavailable", e);
                    pendingFileCallback = null;
                    Toast.makeText(MainActivity.this, "未找到可用的文件选择器", Toast.LENGTH_SHORT).show();
                    return false;
                }
            }
        });
        web.setWebViewClient(new WebViewClient() {
            @Override
            public void onPageFinished(WebView view, String url) {
                view.evaluateJavascript(
                        "window.__ASTRALPATH_API__=\"" + API_BASE.substring(0, API_BASE.length() - 1) + "\";"
                                + "window.__ASTRALPATH_ANDROID__=true;", null);
            }
        });

        String html = readAssetText("www/index.html");
        if (html == null) {
            html = readAssetText("www/mobile.html");
        }
        if (html == null) {
            web.loadDataWithBaseURL(null, "<h3>缺少 mobile.html</h3>", "text/html", "utf-8", null);
            return;
        }
        web.loadDataWithBaseURL(API_BASE, html, "text/html", "utf-8", null);
    }

    private String readAssetText(String path) {
        try (InputStream in = getAssets().open(path);
             BufferedReader br = new BufferedReader(new InputStreamReader(in, StandardCharsets.UTF_8))) {
            StringBuilder sb = new StringBuilder();
            String line;
            while ((line = br.readLine()) != null) sb.append(line).append('\n');
            return sb.toString();
        } catch (Exception e) {
            Log.e(TAG, "readAsset", e);
            return null;
        }
    }

    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        if (keyCode == KeyEvent.KEYCODE_BACK && web != null && web.canGoBack()) {
            web.goBack();
            return true;
        }
        return super.onKeyDown(keyCode, event);
    }

    /**
     * 接收文件选择结果并回填给 WebView（配合 onShowFileChooser）。
     * 说明：保留 startActivityForResult/onActivityResult 而非 androidx ActivityResult API，
     * 因为本壳继承 android.app.Activity 且不依赖 androidx.activity；行为一致、无额外依赖。
     */
    @Override
    protected void onActivityResult(int requestCode, int resultCode, android.content.Intent data) {
        if (requestCode == REQ_FILE_CHOOSER) {
            android.net.Uri[] result = null;
            if (resultCode == RESULT_OK && data != null) {
                android.content.ClipData clip = data.getClipData();
                if (clip != null) {                       // 多选
                    result = new android.net.Uri[clip.getItemCount()];
                    for (int i = 0; i < clip.getItemCount(); i++) {
                        result[i] = clip.getItemAt(i).getUri();
                    }
                } else if (data.getData() != null) {      // 单选
                    result = new android.net.Uri[] { data.getData() };
                }
            }
            if (pendingFileCallback != null) {
                pendingFileCallback.onReceiveValue(result);   // 取消时回传 null，Web 端会收到"未选择文件"
                pendingFileCallback = null;
            }
            return;
        }
        super.onActivityResult(requestCode, resultCode, data);
    }

    @Override
    protected void onDestroy() {
        if (pendingFileCallback != null) {           // 避免回调悬挂导致 WebView 报错
            pendingFileCallback.onReceiveValue(null);
            pendingFileCallback = null;
        }
        if (web != null) {
            web.setWebChromeClient(null);
            web.destroy();
        }
        super.onDestroy();
    }
}
