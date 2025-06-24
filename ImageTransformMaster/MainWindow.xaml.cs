using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ImageTransformMaster
{
    public partial class MainWindow : Window
    {
        private enum SelectionState
        {
            Inactive,
            SelectingSource,
            SelectingDestination
        }

        private SelectionState _currentSelectionState = SelectionState.Inactive;
        private WriteableBitmap _originalBitmap;
        private readonly PointCollection _sourcePoints = new PointCollection();
        private readonly PointCollection _destinationPoints = new PointCollection();
        private readonly List<UIElement> _sourceDots = new List<UIElement>();
        private readonly List<UIElement> _destinationDots = new List<UIElement>();

        public MainWindow()
        {
            InitializeComponent();
            ProcessedImage.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        private void LoadImage_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "Image files (*.png;*.jpeg;*.jpg;*.bmp)|*.png;*.jpeg;*.jpg;*.bmp|All files (*.*)|*.*"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    BitmapImage bmp = new BitmapImage(new Uri(openFileDialog.FileName));
                    _originalBitmap = new WriteableBitmap(bmp);
                    OriginalImage.Source = _originalBitmap;
                    ProcessedImage.Source = _originalBitmap.Clone();
                    ResetAllTransformsAndSelections();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка при загрузке изображения: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ApplyTransforms_Click(object sender, RoutedEventArgs e)
        {
            if (OriginalImage.Source == null)
            {
                MessageBox.Show("Сначала загрузите изображение.", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                ProcessedImage.Source = _originalBitmap.Clone();

                var transformGroup = new TransformGroup();

                double scaleX = double.Parse(ScaleXTextBox.Text, CultureInfo.InvariantCulture);
                double scaleY = double.Parse(ScaleYTextBox.Text, CultureInfo.InvariantCulture);

                switch (FlipComboBox.SelectedIndex)
                {
                    case 1: scaleX *= -1; break;
                    case 2: scaleY *= -1; break;
                    case 3: scaleX *= -1; scaleY *= -1; break;
                }
                transformGroup.Children.Add(new ScaleTransform(scaleX, scaleY));

                transformGroup.Children.Add(new RotateTransform(double.Parse(AngleTextBox.Text, CultureInfo.InvariantCulture)));

                transformGroup.Children.Add(new TranslateTransform(
                    double.Parse(ShiftXTextBox.Text, CultureInfo.InvariantCulture),
                    double.Parse(ShiftYTextBox.Text, CultureInfo.InvariantCulture)
                ));

                ProcessedImage.RenderTransformOrigin = new Point(
                    double.Parse(CenterXTextBox.Text, CultureInfo.InvariantCulture),
                    double.Parse(CenterYTextBox.Text, CultureInfo.InvariantCulture)
                );
                ProcessedImage.RenderTransform = transformGroup;

                RenderOptions.SetBitmapScalingMode(ProcessedImage,
                    BilinearFilterCheckBox.IsChecked == true ? BitmapScalingMode.HighQuality : BitmapScalingMode.LowQuality);
            }
            catch (FormatException)
            {
                MessageBox.Show("Пожалуйста, введите корректные числовые значения (используйте точку как разделитель).", "Ошибка ввода", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StartProjection_Click(object sender, RoutedEventArgs e)
        {
            if (_originalBitmap == null)
            {
                MessageBox.Show("Сначала загрузите изображение.", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ResetAllTransformsAndSelections();
            _currentSelectionState = SelectionState.SelectingSource;
            InstructionText.Text = "Выберите 4 точки на ИСХОДНОМ изображении (слева).";
        }

        // Общий обработчик для обоих изображений
        private void Image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_currentSelectionState == SelectionState.Inactive) return;

            var image = (Image)sender;
            var point = e.GetPosition(image);

            if (_currentSelectionState == SelectionState.SelectingSource && image.Name == "OriginalImage")
            {
                if (_sourcePoints.Count < 4)
                {
                    _sourcePoints.Add(point);
                    DrawPoint(SourceGrid, point, _sourceDots);
                    InstructionText.Text = $"Исходное изображение: выбрано {_sourcePoints.Count} из 4 точек.";
                    if (_sourcePoints.Count == 4)
                    {
                        _currentSelectionState = SelectionState.SelectingDestination;
                        InstructionText.Text = "Теперь выберите 4 соответствующие точки на ПРАВОЙ панели.";
                    }
                }
            }
            else if (_currentSelectionState == SelectionState.SelectingDestination && image.Name == "ProcessedImage")
            {
                if (_destinationPoints.Count < 4)
                {
                    _destinationPoints.Add(point);
                    DrawPoint(DestinationGrid, point, _destinationDots);
                    InstructionText.Text = $"Область назначения: выбрано {_destinationPoints.Count} из 4 точек.";
                    if (_destinationPoints.Count == 4)
                    {
                        _currentSelectionState = SelectionState.Inactive;
                        InstructionText.Text = "Все точки выбраны. Выполняется проекция...";
                        PerformProjection();
                    }
                }
            }
        }

        private void PerformProjection()
        {
            ProcessedImage.RenderTransform = Transform.Identity;

            int width = (int)DestinationGrid.ActualWidth;
            int height = (int)DestinationGrid.ActualHeight;
            if (width == 0 || height == 0) return; // Предотвращение ошибки при скрытом окне

            var projectedBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);

            var homography = CalculateHomography(_sourcePoints, _destinationPoints);
            var inverseHomography = InvertMatrix(homography);

            if (inverseHomography == null)
            {
                InstructionText.Text = "Ошибка: не удалось вычислить проекцию. Точки могут быть коллинеарными.";
                return;
            }

            try
            {
                projectedBitmap.Lock();
                unsafe
                {
                    IntPtr pBackBuffer = projectedBitmap.BackBuffer;
                    int stride = projectedBitmap.BackBufferStride;

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            double w = inverseHomography[2, 0] * x + inverseHomography[2, 1] * y + inverseHomography[2, 2];
                            double sourceX = (inverseHomography[0, 0] * x + inverseHomography[0, 1] * y + inverseHomography[0, 2]) / w;
                            double sourceY = (inverseHomography[1, 0] * x + inverseHomography[1, 1] * y + inverseHomography[1, 2]) / w;

                            if (sourceX >= 0 && sourceX < _originalBitmap.PixelWidth - 1 && sourceY >= 0 && sourceY < _originalBitmap.PixelHeight - 1)
                            {
                                Color c = GetBilinearFilteredPixelColor(_originalBitmap, sourceX, sourceY);
                                int* pPixel = (int*)(pBackBuffer + y * stride + x * 4);
                                *pPixel = (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;
                            }
                        }
                    }
                }
            }
            finally
            {
                projectedBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                projectedBitmap.Unlock();
            }

            ProcessedImage.Source = projectedBitmap;
            InstructionText.Text = "Проекция завершена.";
        }

        private Color GetBilinearFilteredPixelColor(WriteableBitmap bmp, double x, double y)
        {
            int x1 = (int)x;
            int y1 = (int)y;
            int x2 = x1 + 1;
            int y2 = y1 + 1;

            double dx = x - x1;
            double dy = y - y1;

            Color c11 = GetPixelColor(bmp, x1, y1);
            Color c12 = GetPixelColor(bmp, x1, y2);
            Color c21 = GetPixelColor(bmp, x2, y1);
            Color c22 = GetPixelColor(bmp, x2, y2);

            byte r = (byte)((1 - dx) * (1 - dy) * c11.R + dx * (1 - dy) * c21.R + (1 - dx) * dy * c12.R + dx * dy * c22.R);
            byte g = (byte)((1 - dx) * (1 - dy) * c11.G + dx * (1 - dy) * c21.G + (1 - dx) * dy * c12.G + dx * dy * c22.G);
            byte b = (byte)((1 - dx) * (1 - dy) * c11.B + dx * (1 - dy) * c21.B + (1 - dx) * dy * c12.B + dx * dy * c22.B);
            byte a = (byte)((1 - dx) * (1 - dy) * c11.A + dx * (1 - dy) * c21.A + (1 - dx) * dy * c12.A + dx * dy * c22.A);

            return Color.FromArgb(a, r, g, b);
        }

        private Color GetPixelColor(WriteableBitmap bmp, int x, int y)
        {
            if (x < 0 || x >= bmp.PixelWidth || y < 0 || y >= bmp.PixelHeight)
                return Colors.Transparent;

            try
            {
                unsafe
                {
                    bmp.Lock();
                    IntPtr pBackBuffer = bmp.BackBuffer;
                    int stride = bmp.BackBufferStride;
                    int* pPixel = (int*)(pBackBuffer + y * stride + x * 4);
                    int color = *pPixel;
                    bmp.Unlock();
                    return Color.FromArgb((byte)((color >> 24) & 0xFF), (byte)((color >> 16) & 0xFF), (byte)((color >> 8) & 0xFF), (byte)(color & 0xFF));
                }
            }
            catch { return Colors.Transparent; }
        }

        #region Matrix Math

        private double[,] CalculateHomography(PointCollection src, PointCollection dst)
        {
            double[,] A = new double[8, 8];
            double[] b = new double[8];

            for (int i = 0; i < 4; i++)
            {
                A[i * 2, 0] = src[i].X; A[i * 2, 1] = src[i].Y; A[i * 2, 2] = 1; A[i * 2, 3] = 0; A[i * 2, 4] = 0; A[i * 2, 5] = 0; A[i * 2, 6] = -src[i].X * dst[i].X; A[i * 2, 7] = -src[i].Y * dst[i].X;
                A[i * 2 + 1, 0] = 0; A[i * 2 + 1, 1] = 0; A[i * 2 + 1, 2] = 0; A[i * 2 + 1, 3] = src[i].X; A[i * 2 + 1, 4] = src[i].Y; A[i * 2 + 1, 5] = 1; A[i * 2 + 1, 6] = -src[i].X * dst[i].Y; A[i * 2 + 1, 7] = -src[i].Y * dst[i].Y;
                b[i * 2] = dst[i].X; b[i * 2 + 1] = dst[i].Y;
            }

            double[] h = SolveLinearSystem(A, b);
            if (h == null) return null;

            return new double[3, 3] { { h[0], h[1], h[2] }, { h[3], h[4], h[5] }, { h[6], h[7], 1 } };
        }

        private double[] SolveLinearSystem(double[,] M, double[] b)
        {
            int n = b.Length;
            for (int i = 0; i < n; i++)
            {
                int max = i;
                for (int j = i + 1; j < n; j++) if (Math.Abs(M[j, i]) > Math.Abs(M[max, i])) max = j;
                for (int k = i; k < n; k++) { double temp = M[i, k]; M[i, k] = M[max, k]; M[max, k] = temp; }
                double t = b[i]; b[i] = b[max]; b[max] = t;
                if (Math.Abs(M[i, i]) <= 1e-10) return null;
                for (int j = i + 1; j < n; j++)
                {
                    double factor = M[j, i] / M[i, i];
                    b[j] -= factor * b[i];
                    for (int k = i; k < n; k++) M[j, k] -= factor * M[i, k];
                }
            }
            double[] solution = new double[n];
            for (int i = n - 1; i >= 0; i--)
            {
                double sum = 0;
                for (int j = i + 1; j < n; j++) sum += M[i, j] * solution[j];
                solution[i] = (b[i] - sum) / M[i, i];
            }
            return solution;
        }

        private double[,] InvertMatrix(double[,] M)
        {
            if (M == null) return null;
            double det = M[0, 0] * (M[1, 1] * M[2, 2] - M[2, 1] * M[1, 2]) - M[0, 1] * (M[1, 0] * M[2, 2] - M[1, 2] * M[2, 0]) + M[0, 2] * (M[1, 0] * M[2, 1] - M[1, 1] * M[2, 0]);
            if (Math.Abs(det) < 1e-10) return null;
            double invDet = 1.0 / det;
            double[,] result = new double[3, 3];
            result[0, 0] = (M[1, 1] * M[2, 2] - M[2, 1] * M[1, 2]) * invDet; result[0, 1] = (M[0, 2] * M[2, 1] - M[0, 1] * M[2, 2]) * invDet; result[0, 2] = (M[0, 1] * M[1, 2] - M[0, 2] * M[1, 1]) * invDet;
            result[1, 0] = (M[1, 2] * M[2, 0] - M[1, 0] * M[2, 2]) * invDet; result[1, 1] = (M[0, 0] * M[2, 2] - M[0, 2] * M[2, 0]) * invDet; result[1, 2] = (M[1, 0] * M[0, 2] - M[0, 0] * M[1, 2]) * invDet;
            result[2, 0] = (M[1, 0] * M[2, 1] - M[2, 0] * M[1, 1]) * invDet; result[2, 1] = (M[2, 0] * M[0, 1] - M[0, 0] * M[2, 1]) * invDet; result[2, 2] = (M[0, 0] * M[1, 1] - M[1, 0] * M[0, 1]) * invDet;
            return result;
        }

        #endregion

        #region Helpers

        private void ResetAllTransformsAndSelections()
        {
            if (OriginalImage.Source != null)
                ProcessedImage.Source = _originalBitmap.Clone();

            ProcessedImage.RenderTransform = Transform.Identity;

            _sourceDots.ForEach(dot => SourceGrid.Children.Remove(dot));
            _sourceDots.Clear();
            _destinationDots.ForEach(dot => DestinationGrid.Children.Remove(dot));
            _destinationDots.Clear();

            _sourcePoints.Clear();
            _destinationPoints.Clear();
            _currentSelectionState = SelectionState.Inactive;
            InstructionText.Text = "Начните новую операцию.";
        }

        private void DrawPoint(Grid parentGrid, Point center, List<UIElement> dotsList)
        {
            var dot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = Brushes.Red,
                Stroke = Brushes.White,
                StrokeThickness = 1,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(center.X - 5, center.Y - 5, 0, 0)
            };
            parentGrid.Children.Add(dot);
            dotsList.Add(dot);
        }

        #endregion
    }
}